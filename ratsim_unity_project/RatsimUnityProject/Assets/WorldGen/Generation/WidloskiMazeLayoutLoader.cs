using System.Collections.Generic;
using System.Linq;
using UnityEngine;

/// <summary>
/// Dedicated layout provider for the barrier-maze foraging apparatus of
/// Widloski &amp; Foster 2022 (Neuron). Active when <c>layout/mode = widloski_maze</c>.
///
/// Unlike <see cref="MazeLayoutLoader"/> (which materialises walls as chunky
/// cell-sized cubes from a binary mask), this emits the paper's open field: an
/// N×N regular grid of "well-rooms" separated by THIN, reconfigurable barriers on
/// the inter-room grid lines. The barrier "jail-bar" concept is from Ólafsdóttir
/// et al. 2015; the reconfigurable-barrier + reward-well-grid apparatus is
/// Widloski &amp; Foster's own — hence the name.
///
/// What it emits (all eager in <see cref="Generate"/> — the arena is bounded, no
/// chunk streaming):
///   - N×N rooms. Each grid cell IS a room: a footprint-only
///     <see cref="WorldStructure"/> (via <see cref="RoomStructureBuilder"/>) with a
///     single center spawn slot, labelled <c>widloski_maze/room_type</c>
///     (default "well_room"). Because it uses the SAME room-structure builder as
///     the maze rooms, WellLoader/RewardObjectLoader target them unchanged
///     (<c>wells/allowed_structures: well_room</c>, min/max_per_structure = 1).
///   - Thin barriers on inter-room edges. An N×N grid has 2·N·(N−1) edge slots
///     (12 for 3×3). A per-episode barrier seed picks a subset
///     (<c>widloski_maze/n_barriers</c> or <c>widloski_maze/barrier_fraction</c>)
///     and reshuffles it every reset — the paper's per-session reconfiguration.
///     Barriers are switchable, stretchable prefabs (see barrier params).
///
/// Connectivity: after picking a barrier subset we BFS the cell graph (a barrier
/// on a shared edge blocks that step). With <c>widloski_maze/margin_enabled = 1</c>
/// all perimeter cells are additionally connected via the outer open margin band
/// (modelled as a virtual ring node), so the agent can route around blocked
/// interior edges. If any room is unreachable we reshuffle the barrier seed and
/// retry up to <c>widloski_maze/n_tries</c> times, then error loudly.
///
/// The perimeter wall is spawned by <see cref="WorldBoundaryLoader"/> as usual;
/// set <c>world_bounds/width|height = grid_n·cell_size + 2·margin</c> in the preset
/// so it hugs the arena (the room block is always centered on world origin).
///
/// Config params (under "widloski_maze/" unless noted):
///   layout/mode                              -- must be "widloski_maze" to activate
///   widloski_maze/grid_n                     -- N (→ N×N rooms). Default 3.
///   widloski_maze/cell_size                  -- room side / well spacing (world units). Default 5.
///   widloski_maze/margin                     -- open band between room block and perimeter. Default = cell_size.
///   widloski_maze/margin_enabled             -- 0/1; perimeter routing on/off. Default 1.
///   widloski_maze/n_barriers                 -- exact barrier count (-1 = use fraction). Default -1.
///   widloski_maze/barrier_fraction           -- [0,1] fraction of the 2N(N-1) slots. Default 0.5 (paper 6/12).
///   widloski_maze/n_tries                    -- reachability reshuffle attempts. Default 100.
///   widloski_maze/room_type                  -- room structureType label. Default "well_room".
///   widloski_maze/home_cell_mode             -- pick the Home cell: none | center | random | explicit. Default none.
///                                               The Home room gets a distinct type so agent-spawn config can target
///                                               it (agents_spawn_pos/allowed_structures) and the scheduler
///                                               (well_schedule/home_source=structure) can find the Home well.
///                                               INDEPENDENT of where the agent spawns.
///   widloski_maze/home_room_type             -- structureType for the Home room. Default "home_well_room".
///   widloski_maze/home_interior_only         -- 0/1; random mode only: restrict Home to non-edge
///                                               (interior) cells. 3×3 → center only; 5×5 → inner 3×3.
///                                               Needs grid_n≥3 (else ignored). Default 0.
///   widloski_maze/home_cell_gx, home_cell_gz -- Home cell coords when home_cell_mode=explicit. Default 0,0.
///   barriers/prefab_name                     -- Resources/WorldGen/BarrierPrefabs/<name>. Default "barrier_basic".
///   barriers/height                          -- barrier Y size (world units). Default 3.
///   barriers/thickness                       -- barrier size across the edge. Default 0.3.
///   barriers/stretch_walls_to_fit_cell_size  -- 0/1; if 1, along-edge span = cell_size × percentage. Default 1.
///   barriers/stretch_walls_cell_size_percentage -- along-edge span as fraction of cell side. Default 1.0 (paper ≈ 0.85).
///   barriers/length                          -- along-edge span when stretch=0 (0 → cell_size). Default 0.
/// </summary>
public class WidloskiMazeLayoutLoader : WorldDataProvider, ILayoutProvider, IRoomProvider {

    public override WorldDataType[] Provides  => new[] { WorldDataType.Layout };
    public override WorldDataType[] DependsOn => new[] { WorldDataType.Height };

    [Header("Debug")]
    public bool verbose = false;

    private const string BarrierPrefabFolder = "WorldGen/BarrierPrefabs/";

    // ── Params (loaded each episode) ──
    private int    _gridN;
    private float  _cellSize;
    private float  _margin;
    private bool   _marginEnabled;
    private int    _nBarriers;         // resolved absolute count
    private int    _nTries;
    private string _roomType;
    private string _homeRoomType;      // label for the single Home room (distinct structure type)
    private string _homeCellMode;      // none | center | random | explicit
    private bool   _homeInteriorOnly;  // random mode: restrict Home to non-edge (interior) cells
    private Vector2Int _homeCell = new Vector2Int(-1, -1); // (-1,-1) = no Home room this episode

    private string _barrierPrefabName;
    private float  _barrierHeight;
    private float  _barrierThickness;
    private bool   _stretchBarriers;
    private float  _stretchPct;
    private float  _barrierLength;
    private GameObject _barrierPrefab;

    // ── Runtime state ──
    private bool _active;
    private bool _generated;
    private readonly List<Bounds2D> _rooms = new List<Bounds2D>();
    private Transform _barrierRoot;

    // ─────────────────────────────────────────────
    //  WorldDataProvider
    // ─────────────────────────────────────────────

    protected override void OnEnable() {
        base.OnEnable();
        // IRoomProvider: safe to register unconditionally (returns empty when inactive).
        // ILayoutProvider: register only in Generate() when active, to avoid stomping
        // WorldLayoutLoader's registration in default mode.
        WorldServices.Register<IRoomProvider>(this);
    }

    public override void Generate() {
        if (_generated) return;

        string layoutMode = WorldLoadingController.GetParamString("layout/mode", "default");
        _active = layoutMode.Equals("widloski_maze", System.StringComparison.OrdinalIgnoreCase);
        if (!_active) {
            if (verbose) Debug.Log($"WidloskiMazeLayoutLoader: layout/mode='{layoutMode}', inactive");
            return;
        }

        WorldServices.Register<ILayoutProvider>(this);
        LoadParams();

        if (_barrierPrefab == null && _nBarriers > 0) {
            WorldGenStatus.Error("WidloskiMazeLayoutLoader",
                $"barrier prefab '{_barrierPrefabName}' not found in Resources/{BarrierPrefabFolder} — " +
                $"set barriers/prefab_name to a prefab there (or n_barriers/barrier_fraction to 0).");
            return;
        }

        SpawnRooms();
        PlaceBarriers();

        _generated = true;
        Physics.SyncTransforms();
        WorldServices.Get<IHeightProvider>().ProcessTerrainModifications();

        if (verbose)
            Debug.Log($"WidloskiMazeLayoutLoader: {_gridN}x{_gridN} rooms, {_nBarriers} barriers, " +
                      $"cell_size={_cellSize}, margin={_margin}");
    }

    public override void Clear() {
        for (int i = transform.childCount - 1; i >= 0; i--)
            DestroyImmediate(transform.GetChild(i).gameObject);
        _rooms.Clear();
        _barrierRoot = null;
        _generated = false;
        _active = false;
    }

    // ─────────────────────────────────────────────
    //  ILayoutProvider / IRoomProvider
    // ─────────────────────────────────────────────

    public List<EntryPoint> GetEntryPoints(WorldStructure s) => new List<EntryPoint>();

    public bool IsInRoom(float x, float z) {
        Vector2 p = new Vector2(x, z);
        for (int i = 0; i < _rooms.Count; i++)
            if (_rooms[i].Contains(p)) return true;
        return false;
    }

    public List<Bounds2D> GetRooms() => new List<Bounds2D>(_rooms);

    // ─────────────────────────────────────────────
    //  Params
    // ─────────────────────────────────────────────

    private void LoadParams() {
        _gridN         = Mathf.Max(1, WorldLoadingController.GetParamInt("widloski_maze/grid_n", 3));
        _cellSize      = WorldLoadingController.GetParamFloat("widloski_maze/cell_size", 5f);
        _margin        = WorldLoadingController.GetParamFloat("widloski_maze/margin", _cellSize);
        _marginEnabled = WorldLoadingController.GetParamInt("widloski_maze/margin_enabled", 1) != 0;
        _nTries        = Mathf.Max(1, WorldLoadingController.GetParamInt("widloski_maze/n_tries", 100));
        _roomType      = WorldLoadingController.GetParamString("widloski_maze/room_type", "well_room");
        _homeRoomType  = WorldLoadingController.GetParamString("widloski_maze/home_room_type", "home_well_room");
        _homeCellMode  = WorldLoadingController.GetParamString("widloski_maze/home_cell_mode", "none").ToLowerInvariant();
        _homeInteriorOnly = WorldLoadingController.GetParamInt("widloski_maze/home_interior_only", 0) != 0;

        if (_cellSize <= 0f) { Debug.LogError("WidloskiMazeLayoutLoader: cell_size must be > 0"); _cellSize = 5f; }

        int totalEdges = 2 * _gridN * (_gridN - 1);
        int explicitN  = WorldLoadingController.GetParamInt("widloski_maze/n_barriers", -1);
        if (explicitN >= 0) {
            _nBarriers = explicitN;
        } else {
            float frac = WorldLoadingController.GetParamFloat("widloski_maze/barrier_fraction", 0.5f);
            _nBarriers = Mathf.RoundToInt(Mathf.Clamp01(frac) * totalEdges);
        }
        _nBarriers = Mathf.Clamp(_nBarriers, 0, totalEdges);

        _barrierPrefabName = WorldLoadingController.GetParamString("barriers/prefab_name", "barrier_basic");
        _barrierHeight     = WorldLoadingController.GetParamFloat("barriers/height", 3f);
        _barrierThickness  = WorldLoadingController.GetParamFloat("barriers/thickness", 0.3f);
        _stretchBarriers   = WorldLoadingController.GetParamInt("barriers/stretch_walls_to_fit_cell_size", 1) != 0;
        _stretchPct        = WorldLoadingController.GetParamFloat("barriers/stretch_walls_cell_size_percentage", 1f);
        _barrierLength     = WorldLoadingController.GetParamFloat("barriers/length", 0f);

        _barrierPrefab = string.IsNullOrEmpty(_barrierPrefabName)
            ? null
            : Resources.Load<GameObject>(BarrierPrefabFolder + _barrierPrefabName);
    }

    // ─────────────────────────────────────────────
    //  Geometry helpers (room block centered on world origin)
    // ─────────────────────────────────────────────

    private float CellCenterX(int gx) => (gx - (_gridN - 1) * 0.5f) * _cellSize;
    private float CellCenterZ(int gz) => (gz - (_gridN - 1) * 0.5f) * _cellSize;

    // ─────────────────────────────────────────────
    //  Rooms
    // ─────────────────────────────────────────────

    /// <summary>
    /// Picks the Home cell (independent of where the agent spawns). The chosen room is
    /// labelled <c>home_room_type</c> so the agent-spawn config can target it
    /// (<c>agents_spawn_pos/allowed_structures: home_well_room</c>) and the schedule
    /// controller can identify the Home well by that structure. Modes:
    ///   none     → no Home room this episode (all rooms are room_type).
    ///   center   → the central cell ((N-1)/2, (N-1)/2).
    ///   random   → a seeded random cell, re-drawn each episode (paper: pseudorandom per session).
    ///   explicit → widloski_maze/home_cell_gx, home_cell_gz.
    /// </summary>
    private Vector2Int ChooseHomeCell() {
        switch (_homeCellMode) {
            case "none":
            case "":
                return new Vector2Int(-1, -1);
            case "center": {
                int c = (_gridN - 1) / 2;
                return new Vector2Int(c, c);
            }
            case "random": {
                System.Random rng = new System.Random(WorldLoadingController.GetDerivedSeed("widloski_home"));
                // home_interior_only: draw from the inner (N-2)×(N-2) block, i.e. exclude
                // perimeter cells. 3×3 → only the center (1,1); 5×5 → the inner 3×3
                // (gx,gz ∈ [1,3]). Needs N≥3 to have any interior; otherwise fall back
                // to the full grid (with a warning) since there is nothing to restrict to.
                if (_homeInteriorOnly && _gridN >= 3)
                    return new Vector2Int(1 + rng.Next(_gridN - 2), 1 + rng.Next(_gridN - 2));
                if (_homeInteriorOnly)
                    Debug.LogWarning($"WidloskiMazeLayoutLoader: home_interior_only=1 but grid_n={_gridN} " +
                                     $"has no interior cells; drawing Home from the full grid.");
                return new Vector2Int(rng.Next(_gridN), rng.Next(_gridN));
            }
            case "explicit": {
                int gx = Mathf.Clamp(WorldLoadingController.GetParamInt("widloski_maze/home_cell_gx", 0), 0, _gridN - 1);
                int gz = Mathf.Clamp(WorldLoadingController.GetParamInt("widloski_maze/home_cell_gz", 0), 0, _gridN - 1);
                return new Vector2Int(gx, gz);
            }
            default:
                Debug.LogWarning($"WidloskiMazeLayoutLoader: unknown home_cell_mode '{_homeCellMode}'; treating as 'none'");
                return new Vector2Int(-1, -1);
        }
    }

    private void SpawnRooms() {
        IHeightProvider heights = WorldServices.Get<IHeightProvider>();
        Vector2 size = new Vector2(_cellSize, _cellSize);
        _homeCell = ChooseHomeCell();

        for (int gz = 0; gz < _gridN; gz++) {
            for (int gx = 0; gx < _gridN; gx++) {
                float cx = CellCenterX(gx);
                float cz = CellCenterZ(gz);
                float cy = heights.GetTerrainHeight(cx, cz);

                // One center slot; name carries the grid coord so WellLoader parses it
                // into WellData.gridCoord (the schedule controller / logging can use it).
                var slots = new List<RoomStructureBuilder.Slot> {
                    new RoomStructureBuilder.Slot {
                        name = $"cell_{gx}_{gz}",
                        position = new Vector3(cx, cy, cz)
                    }
                };

                // The Home cell gets a distinct structure type so spawn/schedule can target it.
                string type = (gx == _homeCell.x && gz == _homeCell.y) ? _homeRoomType : _roomType;
                RoomStructureBuilder.Build(
                    transform, type, new Vector3(cx, cy, cz), size, slots);
                _rooms.Add(new Bounds2D(new Vector2(cx, cz), size, 0f));
            }
        }

        if (verbose && _homeCell.x >= 0)
            Debug.Log($"WidloskiMazeLayoutLoader: Home room ({_homeRoomType}) at cell ({_homeCell.x},{_homeCell.y})");
    }

    // ─────────────────────────────────────────────
    //  Barriers
    // ─────────────────────────────────────────────
    //
    // Edge indexing: vertical edges (between horizontally-adjacent cells) come
    // first, then horizontal edges. This lets a barrier subset be a simple set of
    // ints, and the BFS decode which edge a step crosses in O(1).
    //   vertical  edge (gx,gz)|(gx+1,gz): id = gz*(N-1) + gx,   gx∈[0,N-2], gz∈[0,N-1]
    //   horizontal edge (gx,gz)|(gx,gz+1): id = vCount + gz*N + gx, gx∈[0,N-1], gz∈[0,N-2]

    private int VCount => _gridN * (_gridN - 1);
    private int VId(int gx, int gz) => gz * (_gridN - 1) + gx;
    private int HId(int gx, int gz) => VCount + gz * _gridN + gx;

    private void PlaceBarriers() {
        if (_nBarriers <= 0 || _barrierPrefab == null) return;

        int totalEdges = 2 * _gridN * (_gridN - 1);
        int baseSeed = WorldLoadingController.GetDerivedSeed("widloski_barriers");

        HashSet<int> chosen = null;
        int usedAttempt = 0;
        for (int attempt = 0; attempt < _nTries; attempt++) {
            usedAttempt = attempt + 1;
            // Fixed master seed → identical retry sequence. SeedMix (not System.HashCode, which
            // is re-salted on every process start / domain reload) avoids the overlapping-coset
            // problem plain XOR has across adjacent seeds.
            System.Random rng = new System.Random(SeedMix.Combine(baseSeed, attempt));
            HashSet<int> candidate = PickBarrierSubset(totalEdges, _nBarriers, rng);
            if (AllRoomsReachable(candidate)) { chosen = candidate; break; }
        }

        if (chosen == null) {
            WorldGenStatus.Error("WidloskiMazeLayoutLoader",
                $"could not place {_nBarriers} barriers on a {_gridN}x{_gridN} grid without isolating a " +
                $"room after {_nTries} attempts (margin_enabled={_marginEnabled}). " +
                $"Lower n_barriers/barrier_fraction, enable margin routing, or raise n_tries.");
            return;
        }

        _barrierRoot = new GameObject("Barriers").transform;
        _barrierRoot.SetParent(transform, false);

        foreach (int id in chosen) SpawnBarrier(id);

        if (verbose)
            Debug.Log($"WidloskiMazeLayoutLoader: placed {chosen.Count} barriers " +
                      $"(reachable on attempt {usedAttempt}/{_nTries})");
    }

    private HashSet<int> PickBarrierSubset(int totalEdges, int k, System.Random rng) {
        // Partial Fisher-Yates: shuffle first k of an index array, take them.
        int[] idx = new int[totalEdges];
        for (int i = 0; i < totalEdges; i++) idx[i] = i;
        var set = new HashSet<int>();
        for (int i = 0; i < k; i++) {
            int j = rng.Next(i, totalEdges);
            (idx[i], idx[j]) = (idx[j], idx[i]);
            set.Add(idx[i]);
        }
        return set;
    }

    /// <summary>
    /// BFS over the N×N cell graph: a step to an orthogonal neighbour is allowed unless
    /// a barrier sits on the shared edge. When margin routing is enabled, every perimeter
    /// cell also connects to a virtual "margin ring" node (index N*N), so the agent can
    /// travel around the outside. Returns true iff all N*N cells are reachable from cell 0.
    /// </summary>
    private bool AllRoomsReachable(HashSet<int> barriers) {
        int N = _gridN;
        if (N <= 1) return true;

        int ring = N * N;                       // virtual node id
        int nNodes = ring + (_marginEnabled ? 1 : 0);
        bool[] vis = new bool[nNodes];
        var q = new Queue<int>();
        vis[0] = true; q.Enqueue(0);

        while (q.Count > 0) {
            int c = q.Dequeue();

            if (c == ring) {
                // Ring connects to every perimeter cell.
                for (int gz = 0; gz < N; gz++)
                    for (int gx = 0; gx < N; gx++)
                        if (IsPerimeter(gx, gz)) TryVisit(gz * N + gx, vis, q);
                continue;
            }

            int cx = c % N, cz = c / N;
            if (_marginEnabled && IsPerimeter(cx, cz)) TryVisit(ring, vis, q);

            // West  (cx-1): vertical edge (cx-1,cz)
            if (cx > 0     && !barriers.Contains(VId(cx - 1, cz))) TryVisit(c - 1, vis, q);
            // East  (cx+1): vertical edge (cx,cz)
            if (cx < N - 1 && !barriers.Contains(VId(cx, cz)))     TryVisit(c + 1, vis, q);
            // South (cz-1): horizontal edge (cx,cz-1)
            if (cz > 0     && !barriers.Contains(HId(cx, cz - 1))) TryVisit(c - N, vis, q);
            // North (cz+1): horizontal edge (cx,cz)
            if (cz < N - 1 && !barriers.Contains(HId(cx, cz)))     TryVisit(c + N, vis, q);
        }

        for (int i = 0; i < N * N; i++)
            if (!vis[i]) return false;
        return true;
    }

    private static void TryVisit(int node, bool[] vis, Queue<int> q) {
        if (!vis[node]) { vis[node] = true; q.Enqueue(node); }
    }

    private bool IsPerimeter(int gx, int gz) =>
        gx == 0 || gx == _gridN - 1 || gz == 0 || gz == _gridN - 1;

    private void SpawnBarrier(int edgeId) {
        IHeightProvider heights = WorldServices.Get<IHeightProvider>();
        float span = _stretchBarriers
            ? _cellSize * _stretchPct
            : (_barrierLength > 0f ? _barrierLength : _cellSize);

        Vector3 center;
        Vector3 scale;
        if (edgeId < VCount) {
            // Vertical edge between (gx,gz) and (gx+1,gz): wall runs along Z.
            int gx = edgeId % (_gridN - 1);
            int gz = edgeId / (_gridN - 1);
            float x = (gx + 0.5f - (_gridN - 1) * 0.5f) * _cellSize;
            float z = CellCenterZ(gz);
            center = new Vector3(x, 0f, z);
            scale  = new Vector3(_barrierThickness, _barrierHeight, span);
        } else {
            // Horizontal edge between (gx,gz) and (gx,gz+1): wall runs along X.
            int h  = edgeId - VCount;
            int gx = h % _gridN;
            int gz = h / _gridN;
            float x = CellCenterX(gx);
            float z = (gz + 0.5f - (_gridN - 1) * 0.5f) * _cellSize;
            center = new Vector3(x, 0f, z);
            scale  = new Vector3(span, _barrierHeight, _barrierThickness);
        }

        float terrainY = heights.GetTerrainHeight(center.x, center.z);
        center.y = terrainY + _barrierHeight * 0.5f;

        // The prefab is a unit-cube barrier (Default-layer collider + 'barrier' semantic
        // label). Scaling the root sizes both mesh and collider (WorldBoundaryLoader does
        // the same for boundary walls).
        GameObject b = Instantiate(_barrierPrefab, center, Quaternion.identity, _barrierRoot);
        b.transform.localScale = scale;
        b.name = $"barrier_{edgeId}";
    }
}
