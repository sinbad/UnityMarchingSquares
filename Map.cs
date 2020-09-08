using System.Collections.Generic;
using UnityEngine;
using System;
#if UNITY_EDITOR
using UnityEditor;
#endif

[RequireComponent(typeof(MeshGenerator))]
public class Map : MonoBehaviour {
	public int width = 80;
	public int height = 60;

    Vector2 startingPoint;
    public Vector2 StartingPoint { get { return startingPoint; }}

    /// Value of a map cell which means it is totally empty
    public const byte Empty = 0;
    /// Value of a map cell which means it is totally solid
    public const byte Solid = 255;
    /// Threshold between empty and solid (>= means solid)
    public const byte SolidThreshold = 127;
    /// Navigable spaces are low enough that walls are not close
    public const byte NavigableThreshold = 70;

    byte[,] mapData;
    IMapSource mapSource;
    MeshGenerator mapMeshGenerator;
    Vector2 mapWorldMin;
    float mapSquareSize;
    int layerMask;

    void GetReferences() {
        // map source can be one of many just get it
        if (mapSource == null)
            mapSource = GetComponent<IMapSource>();
        if (mapMeshGenerator == null)
            mapMeshGenerator = GetComponent<MeshGenerator>();

        layerMask = LayerMask.GetMask("Map");
    }

	void Awake () {
        GetReferences();
	}

    public void RefreshMap(bool reload) {
        GetReferences();
        if (reload || !mapMeshGenerator.IsMeshBuilt()) {
            mapData = mapSource.GetMapData(width, height, reload);
            mapSquareSize = 100f/(float)mapData.GetLength(1);
            mapWorldMin = mapMeshGenerator.GenerateMesh(mapData, mapSquareSize);
        }
        FindStartPoint();
    }

    public void DestroyMap() {
        GetReferences();
        mapMeshGenerator.Destroy();
    }

    void FindStartPoint() {
        List<List<Vector2>> floors = mapMeshGenerator.GetFloorSegments(3);
        List<Vector2> bestFloor = null;
        for (int i = 0; i < floors.Count; i++) {
            if (bestFloor == null || floors[i].Count > bestFloor.Count) {
                bestFloor = floors[i];
            }
        }
        if (bestFloor != null) {
            startingPoint = bestFloor[bestFloor.Count/2] + new Vector2(0,1);
        } else {
            UberDebug.LogError("Unable to find a starting point on the floor");
        }
    }

    // Convert a map grid point to world position
    public Vector2 GridToWorld(IntVector2 grid)
    {
		return mapWorldMin + new Vector2(grid.x * mapSquareSize, grid.y * mapSquareSize);
	}
    // Convert a list of map grid points to world positions
    public List<Vector2> GridToWorld(List<IntVector2> gridList) {
        var ret = new List<Vector2>(gridList.Count);
        for (int i = 0; i < gridList.Count; ++i) {
            ret.Add(GridToWorld(gridList[i]));
        }
        return ret;
    }
    // Convert a list of points to world space and clamp start/end to correct point
    public List<Vector2> GridToWorldPath(List<IntVector2> gridList, Vector2 worldStart, Vector2 worldEnd) {
        var ret = new List<Vector2>(gridList.Count);
        for (int i = 0; i < gridList.Count; ++i) {
            if (i == 0) {
                ret.Add(worldStart);
            } else if (i == gridList.Count - 1) {
                ret.Add(worldEnd);
            } else {
                ret.Add(GridToWorld(gridList[i]));
            }
        }
        return ret;
    }
    // Convert a list of points to world space and clamp end to correct point
    public List<Vector2> GridToWorldPath(List<IntVector2> gridList, Vector2 worldEnd) {
        var ret = new List<Vector2>(gridList.Count);
        for (int i = 0; i < gridList.Count; ++i) {
            if (i == gridList.Count - 1) {
                ret.Add(worldEnd);
            } else {
                ret.Add(GridToWorld(gridList[i]));
            }
        }
        return ret;
    }
    // Find the nearest map data grid point to a world position
    public  IntVector2 WorldToGrid(Vector2 worldPos)
    {
        Vector2 gridPos = (worldPos - mapWorldMin) / mapSquareSize;
		return new IntVector2((int)gridPos.x, (int)gridPos.y);
    }
    // Convert a world size to grid size
    public float WorldToGrid(float worldSize) {
        return worldSize / mapSquareSize;
    }


    // Return whether this grid position is open space
    public bool IsGridOpenSpace(IntVector2 gridPos) {
        return gridPos.x >= 0 && gridPos.x < mapData.GetLength(0) &&
            gridPos.y >= 0 && gridPos.y < mapData.GetLength(1) &&
            mapData[gridPos.x, gridPos.y] < NavigableThreshold;
    }
    // Return whether this grid position is open space wide enough to accommodate object
    // objectWidthGridSpace is in multiples of grid coords
    public bool IsGridOpenSpace(IntVector2 gridPos, float objectWidthGridSpace) {
        if (!IsGridOpenSpace(gridPos))
            return false;

        if (objectWidthGridSpace > 1.0f) {
            // Need to check adjacent grid points
            float halfWidth = objectWidthGridSpace * 0.5f;
            int extent = Mathf.RoundToInt(halfWidth); // extent at equator
            for (int y = -extent; y <= extent; ++y) {
                for (int x = -extent; x <= extent; ++x) {
                    if (!IsGridOpenSpace(new IntVector2(gridPos.x+x, gridPos.y+y)) &&
                        Mathf.Sqrt(x*x + y*y) <= halfWidth) {
                            return false;
                        }
                }
            }
        }
        return true;
    }

    // Ensure that an input grid point is open space, if not find nearest that is
    public IntVector2 NearestGridOpenSpace(IntVector2 inpos) {
        if (IsGridOpenSpace(inpos)) {
            return inpos;
        }

        // Find nearest open space with breadth search
        var graph = new MapWeightedGraph(this);
        var bfs = new BreadthSearch<IntVector2>();
        Func<IntVector2, bool> f = (pos) => {
            return IsGridOpenSpace(pos);
        };
        var path = bfs.GetPath(inpos, f, graph);

        if (path.Count > 0) {
            return path[path.Count-1];
        }

        UberDebug.LogError("Unable to find any open space in map near {0}! Should never happen", inpos);
        return new IntVector2(0,0);
    }

    // Check whether a 'wide ray' with the given properties would collide with the map
    public bool IsCircleSweepCollision(Vector2 from, Vector2 to, float radius) {
        return CircleSweepCollision(from, to, radius).collider != null;
    }

    // Check whether a 'wide ray' with the given properties would collide with the map
    public RaycastHit2D CircleSweepCollision(Vector2 from, Vector2 to, float radius) {
        Vector3 diff = to - from;
        Vector3 dir = diff.normalized;
        return Physics2D.CircleCast(from, radius, dir, diff.magnitude, layerMask);
    }
}