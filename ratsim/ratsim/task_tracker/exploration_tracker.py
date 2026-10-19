"""Volumetric (2D) exploration tracker.

Builds an occupancy grid from ground-truth pose + 2D lidar scans and tracks
how much area has been "explored" (i.e. moved from UNKNOWN to either FREE or
OCCUPIED).  Used by TaskTracker to reward exploration coverage.

Cell values follow the nav_msgs/OccupancyGrid convention:
  -1  = UNKNOWN
   0  = FREE
  100 = OCCUPIED

The grid is axis-aligned with the ROS world frame (x=forward, y=left) and
centered on (0, 0) — which, per the project's convention, is the center of
the maze / arena.
"""

import math
from typing import List, Optional

import numpy as np


UNKNOWN = -1
FREE = 0
OCCUPIED = 100


def _bresenham_ray(
    x0: int, y0: int, x1: int, y1: int, max_cells: int
) -> np.ndarray:
    """Integer cells on the line from (x0,y0) to (x1,y1), inclusive.

    Returns an (N, 2) int array of (col, row) pairs.  Capped to at most
    ``max_cells`` cells to keep per-ray cost bounded.
    """
    dx = x1 - x0
    dy = y1 - y0
    xsign = 1 if dx > 0 else -1
    ysign = 1 if dy > 0 else -1
    dx = abs(dx)
    dy = abs(dy)
    if dx > dy:
        xx, xy, yx, yy = xsign, 0, 0, ysign
    else:
        dx, dy = dy, dx
        xx, xy, yx, yy = 0, ysign, xsign, 0
    n = min(dx + 1, max_cells)
    out = np.empty((n, 2), dtype=np.int32)
    D = 2 * dy - dx
    y = 0
    for i in range(n):
        out[i, 0] = x0 + i * xx + y * yx
        out[i, 1] = y0 + i * xy + y * yy
        if D >= 0:
            y += 1
            D -= 2 * dx
        D += 2 * dy
    return out


class ExplorationTracker:
    """2D occupancy grid + exploration-area accounting.

    The grid spans a rectangle of size ``world_width`` x ``world_height``
    centered on the origin (maze center in ROS frame).

    ``world_width`` / ``world_height`` follow the worldgen convention of
    ``world_bounds/width|height``: width is the extent along Unity +X and
    height the extent along Unity +Z (see ``WorldLayoutLoader.PlaceStructures``).
    ``CoordConversion`` maps Unity X -> ROS -y and Unity Z -> ROS x, so in the
    ROS frame the grid is ``world_height`` long along x and ``world_width``
    long along y.  (Until 2026-09 this was swapped, so any non-square world
    had most of its area outside the grid.)

    Attributes:
        resolution: cell size in meters (square cells).
        cells_x, cells_y: grid dimensions along ROS x / ROS y.
        origin_x, origin_y: world coords of the (0,0) cell's bottom-left corner.
        grid: int8 array shape (cells_y, cells_x).
    """

    def __init__(
        self,
        world_width: float,
        world_height: float,
        resolution: float = 1.0,
    ):
        self.resolution = float(resolution)
        self.world_width = float(world_width)
        self.world_height = float(world_height)

        # ROS x (Unity Z) spans world_height; ROS y (Unity X) spans world_width.
        self.cells_x = int(math.ceil(self.world_height / self.resolution))
        self.cells_y = int(math.ceil(self.world_width / self.resolution))

        # Origin = bottom-left corner of grid, world centered at (0, 0)
        self.origin_x = -self.cells_x * self.resolution / 2.0
        self.origin_y = -self.cells_y * self.resolution / 2.0

        self.cell_area = self.resolution * self.resolution

        self.grid = np.full((self.cells_y, self.cells_x), UNKNOWN, dtype=np.int8)
        self._known_cell_count = 0

        # Cap ray length to the grid diagonal (in cells) to avoid runaway loops
        self._max_ray_cells = (
            int(math.ceil(math.hypot(self.cells_x, self.cells_y))) + 2
        )

    # ---- coord helpers ---------------------------------------------------

    def world_to_cell(self, wx: float, wy: float) -> "tuple[int, int]":
        col = int((wx - self.origin_x) / self.resolution)
        row = int((wy - self.origin_y) / self.resolution)
        return col, row

    def _in_bounds(self, col: int, row: int) -> bool:
        return 0 <= col < self.cells_x and 0 <= row < self.cells_y

    # ---- state -----------------------------------------------------------

    def reset(self):
        self.grid.fill(UNKNOWN)
        self._known_cell_count = 0

    @property
    def known_area_m2(self) -> float:
        return self._known_cell_count * self.cell_area

    # ---- lidar integration ----------------------------------------------

    def update_from_lidar(
        self,
        agent_x: float,
        agent_y: float,
        agent_yaw: float,
        ranges: "List[float] | np.ndarray",
        angle_start_rad: float,
        angle_increment_rad: float,
        max_range: float,
    ) -> "dict":
        """Integrate one lidar scan.

        Returns a dict::

            {
              "newly_known": int,   # cells that moved UNKNOWN → known
              "rays_total": int,
              "rays_out_of_bounds": int,   # no in-grid cells touched
              "rays_zero_len": int,        # agent cell == endpoint cell
              "agent_in_bounds": bool,
            }

        Free/occupied updates follow the standard ray-cast model:
          * Cells along each ray are marked FREE (if not already OCCUPIED).
          * The endpoint cell is marked OCCUPIED if the range is below
            ``max_range`` (i.e. the ray hit something).
        """
        cos_yaw = math.cos(agent_yaw)
        sin_yaw = math.sin(agent_yaw)
        ac, ar = self.world_to_cell(agent_x, agent_y)
        agent_in_bounds = self._in_bounds(ac, ar)

        newly_known = 0
        rays_total = 0
        rays_oob = 0
        rays_zero = 0

        # Treat a negative/zero reading as "no return" — max-range sweep.
        for i, r in enumerate(ranges):
            rays_total += 1
            angle_local = angle_start_rad + i * angle_increment_rad
            dx_local = math.cos(angle_local)
            dy_local = math.sin(angle_local)
            dx_world = cos_yaw * dx_local - sin_yaw * dy_local
            dy_world = sin_yaw * dx_local + cos_yaw * dy_local

            hit = r is not None and r > 0 and r < max_range
            ray_len = r if hit else max_range

            end_x = agent_x + dx_world * ray_len
            end_y = agent_y + dy_world * ray_len
            ec, er = self.world_to_cell(end_x, end_y)

            if ec == ac and er == ar:
                rays_zero += 1

            cells = _bresenham_ray(ac, ar, ec, er, self._max_ray_cells)

            # Clip to grid bounds vectorized
            cols = cells[:, 0]
            rows = cells[:, 1]
            in_bounds = (
                (cols >= 0) & (cols < self.cells_x)
                & (rows >= 0) & (rows < self.cells_y)
            )
            cols = cols[in_bounds]
            rows = rows[in_bounds]
            if cols.size == 0:
                rays_oob += 1
                continue

            # All but the last in-bounds cell get FREE (unless already OCCUPIED).
            free_mask_rows = rows[:-1]
            free_mask_cols = cols[:-1]

            if free_mask_rows.size > 0:
                existing = self.grid[free_mask_rows, free_mask_cols]
                fill_mask = existing != OCCUPIED
                # count newly known (was UNKNOWN)
                newly_known += int(np.sum(existing[fill_mask] == UNKNOWN))
                target_rows = free_mask_rows[fill_mask]
                target_cols = free_mask_cols[fill_mask]
                self.grid[target_rows, target_cols] = FREE

            # Endpoint
            end_r = rows[-1]
            end_c = cols[-1]
            existing_end = self.grid[end_r, end_c]
            if hit:
                if existing_end == UNKNOWN:
                    newly_known += 1
                self.grid[end_r, end_c] = OCCUPIED
            else:
                # Max-range sweep — treat like free
                if existing_end != OCCUPIED:
                    if existing_end == UNKNOWN:
                        newly_known += 1
                    self.grid[end_r, end_c] = FREE

        self._known_cell_count += newly_known
        return {
            "newly_known": newly_known,
            "rays_total": rays_total,
            "rays_out_of_bounds": rays_oob,
            "rays_zero_len": rays_zero,
            "agent_in_bounds": agent_in_bounds,
        }

    # ---- rendering -------------------------------------------------------

    def to_rgb_image(
        self,
        agent_xy: "Optional[tuple[float, float]]" = None,
    ) -> np.ndarray:
        """Return an (H, W, 3) uint8 RGB image for visualization.

        Unknown = dark gray, free = white, occupied = black.  Oriented to
        match Unity's top-down view: image +x (right) = Unity +X =
        ROS −y, image +y (up) = Unity +Z = ROS +x.  So an agent at Unity
        top-right (both X and Z positive) renders at top-right of the image.
        """
        img = np.full((self.cells_y, self.cells_x, 3), 80, dtype=np.uint8)
        img[self.grid == FREE] = (245, 245, 245)
        img[self.grid == OCCUPIED] = (15, 15, 15)

        if agent_xy is not None:
            ac, ar = self.world_to_cell(agent_xy[0], agent_xy[1])
            if self._in_bounds(ac, ar):
                img[ar, ac] = (230, 40, 40)

        # Internal layout: img[row=ROS y idx, col=ROS x idx].
        # Transpose to (ROS x, ROS y) so axis 0 = ROS x, axis 1 = ROS y.
        # Flip axis 0 so high ROS x (Unity +Z = forward) renders at the top.
        # Flip axis 1 so high ROS y (Unity −X = left) renders on the left.
        # Result is (cells_x, cells_y, 3) = (height cells, width cells, 3):
        # a landscape world renders as a landscape image.
        img = np.transpose(img, (1, 0, 2))
        img = np.flip(img, axis=0)
        img = np.flip(img, axis=1)
        return img
