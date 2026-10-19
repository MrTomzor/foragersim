"""Non-blocking matplotlib viewer for ExplorationTracker's occupancy grid.

Debug window only — intentionally minimal.  Tries an interactive backend
(TkAgg → Qt5Agg → QtAgg) if the current backend is Agg.  Disables itself
cleanly if the display is missing, the backend is non-interactive, or the
user closes the window.
"""

import numpy as np


class ExplorationViewer:
    def __init__(self, title: str = "Exploration", draw_every: int = 1):
        self.enabled = True
        self.draw_every = max(1, int(draw_every))
        self._tick = 0
        self._fig = None
        self._im = None
        self._title_text = title
        self._closed_by_user = False

        try:
            import matplotlib
            # Backends to consider non-interactive (no window).  Note that
            # 'TkAgg', 'Qt5Agg', 'QtAgg', 'GTK3Agg' etc. ARE interactive
            # despite the 'Agg' suffix — only bare 'agg' is headless.
            NON_INTERACTIVE = {
                "agg", "pdf", "ps", "svg", "template", "cairo", "pgf",
                "module://matplotlib_inline.backend_inline",
            }

            cur_backend = matplotlib.get_backend().lower()
            if cur_backend in NON_INTERACTIVE:
                for backend in ("TkAgg", "Qt5Agg", "QtAgg"):
                    try:
                        matplotlib.use(backend, force=True)
                        break
                    except Exception:
                        continue
            import matplotlib.pyplot as plt

            backend = matplotlib.get_backend()
            if backend.lower() in NON_INTERACTIVE:
                print(
                    f"[ExplorationViewer] non-interactive backend '{backend}' — "
                    f"no window will appear. Install python3-tk or a Qt binding "
                    f"to enable the live viewer, or run on a machine with a display."
                )
                self.enabled = False
                return

            plt.ion()
            self._plt = plt
            self._fig, self._ax = plt.subplots(figsize=(5, 5), num=title)
            self._ax.set_title(title)
            self._ax.set_xticks([])
            self._ax.set_yticks([])
            self._fig.tight_layout()

            # Disable self when the figure closes (either user hits the X,
            # or we close it ourselves).  Without this, a later draw call
            # crashes on a destroyed canvas.
            def _on_close(event):
                if self.enabled:
                    print("[ExplorationViewer] window closed — disabling.")
                self.enabled = False
            self._fig.canvas.mpl_connect("close_event", _on_close)

            plt.show(block=False)
            plt.pause(0.05)
            print(f"[ExplorationViewer] active with backend={backend}")
        except Exception as exc:
            print(f"[ExplorationViewer] disabled: {exc}")
            self.enabled = False

    def update(
        self,
        rgb_image: np.ndarray,
        known_area_m2: float,
        total_area_m2: float,
    ):
        if not self.enabled:
            return
        self._tick += 1
        try:
            if self._im is None:
                self._im = self._ax.imshow(rgb_image, interpolation="nearest")
            elif self._tick % self.draw_every == 0:
                self._im.set_data(rgb_image)
            if self._tick % self.draw_every == 0:
                pct = 100.0 * known_area_m2 / total_area_m2 if total_area_m2 > 0 else 0.0
                self._ax.set_title(
                    f"{self._title_text}  —  {known_area_m2:.0f}/{total_area_m2:.0f} m² "
                    f"({pct:.1f}%)"
                )
                self._fig.canvas.draw_idle()
            # Always pump the GUI event loop so the window stays responsive
            # (prevents "Not Responding" auto-kill from the window manager).
            self._plt.pause(0.001)
        except Exception as exc:
            print(f"[ExplorationViewer] draw failed, disabling: {exc}")
            self.enabled = False

    def close(self):
        if self._fig is None:
            return
        try:
            self._plt.close(self._fig)
        except Exception:
            pass
