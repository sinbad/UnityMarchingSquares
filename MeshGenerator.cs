using UnityEngine;
using System.Collections.Generic;
using System;
#if UNITY_EDITOR
using UnityEditor;
#endif

[RequireComponent(typeof(MeshFilter))]
[RequireComponent(typeof(MeshRenderer))]
public class MeshGenerator : MonoBehaviour {

	// Marching squares implementation
	//  A----x----B
	//  |         |
	//  w         y
	//  |         |
	//  D----z----C
	//
	// A, B, C, D = ControlNode
	//              Mapped to the on/off wall states of 4 pixels in the map
	// x, y, z, w = Node
	//              Points used to generate triangles based on ControlNode state
	//

	// This is the temp data used when generating the mesh, which is
	// not serialised and therefore not present when running a level from
	// previously authored / generated data
	private class TemporaryData  {
		public List<Vector3> vertices;
		public List<int> triangles;
		public Vector2 mapWorldMin, mapWorldMax;
		public float mapSquareSize;

		// Vertex edge neighbour map to help us build outlines
		// Map is of vertex index to next clockwise vertex index (when viewed from
		// inside solid area, so anticlockwise when viewed from inside empty space)
		public Dictionary<int, int> vertexEdgeNeighbourMap;
		// List of outlines
		public List<List<int>> outlines;
		public List<bool> verticesOutlineDone;
	}

	TemporaryData tempData;

	public Material edgeMaterial;
	public float edgeWidth;
	public enum TriangulateMode {
		Rows = 0,
		Columns = 1,
		Spiral = 2
	}
	public TriangulateMode triangulateStyle;
	public float triangulateMaxRatio = 3f;

	MeshFilter meshFilter;
	EdgeCollider2D[] edgeColliders;

	void GetReferences() {
		if (meshFilter == null)
			meshFilter = GetComponent<MeshFilter>();
	}

	public bool IsMeshBuilt() {
		GetReferences();
		return meshFilter.mesh != null && meshFilter.mesh.vertexCount > 0;
	}

	public EdgeCollider2D[] GetEdgeColliders() {
		if (edgeColliders == null) {
			edgeColliders = GetComponents<EdgeCollider2D>();
		}
		return edgeColliders;
	}

	public void Destroy() {
		GetReferences();
		meshFilter.mesh = null;
		DestroyEdgeColliders();
		DestroyEdgeFx(0);

	}

	// Generate mesh and return the map world minimum pos for info
	public Vector2 GenerateMesh(byte[,] map, float squareSize) {
		GetReferences();
		tempData = new TemporaryData();

		tempData.mapSquareSize = squareSize;
		SquareGrid squareGrid = new SquareGrid(map, squareSize);
		int width = squareGrid.squares.GetLength(0);
		int height = squareGrid.squares.GetLength(1);

		int nodesX = map.GetLength(0);
		int nodesY = map.GetLength(1);
		float halfSquareSize = squareSize * 0.5f;
		float mapXstart = -squareSize * nodesX * 0.5f + halfSquareSize;
		float mapYstart = -squareSize * nodesY * 0.5f + halfSquareSize;
		tempData.mapWorldMin = new Vector2(mapXstart, mapYstart);
		tempData.mapWorldMax = new Vector2((width-1) * squareSize + mapXstart, (height-1) * squareSize + mapYstart);

		// estimate min capacity required
		// vertices are shared but also along midpoints, max 5 unique, min 2
		tempData.vertices = new List<Vector3>(width*height*3);
		// 2 triangles average?
		tempData.triangles = new List<int>(width*height*6);
		// Vertices -> triangle map same capacity as vertices
		tempData.vertexEdgeNeighbourMap = new Dictionary<int,int>(tempData.vertices.Capacity);
		tempData.verticesOutlineDone = new List<bool>(tempData.vertices.Capacity);


		TriangulateGrid(squareGrid);
		ExpandBoundaryVertices();

		Mesh mesh = new Mesh();
		mesh.vertices = tempData.vertices.ToArray();
		mesh.triangles = tempData.triangles.ToArray();
#if UNITY_EDITOR
		if (Application.isEditor)
			Undo.RecordObject(meshFilter, "Change mesh");
#endif
		meshFilter.mesh = mesh;

		CreateEdges();

		Vector2 ret = tempData.mapWorldMin;
		// Free temp data
		tempData = null;
		return ret;
	}

    void ExpandBoundaryVertices() {
		for (int i = 0; i < tempData.vertices.Count; ++i) {
			Vector3 v = tempData.vertices[i];
			float fudge = 0.001f;
			bool changed = false;
			if (v.x <= tempData.mapWorldMin.x + fudge) {
				v.x -= tempData.mapSquareSize * 100;
				changed = true;
			} else if (v.x + fudge >= tempData.mapWorldMax.x) {
				v.x += tempData.mapSquareSize * 100;
				changed = true;
			}
			if (v.y <= tempData.mapWorldMin.y + fudge) {
				v.y -= tempData.mapSquareSize * 100;
				changed = true;
			} else if (v.y + fudge >= tempData.mapWorldMax.y) {
				v.y += tempData.mapSquareSize * 100;
				changed = true;
			}
			if (changed) {
				tempData.vertices[i] = v;
			}
		}
	}

	void DestroyEdgeColliders() {
#if UNITY_EDITOR
		if (Application.isEditor) {
			// Have to use DestroyImmediate in Editor mode but cannot iterate on array
			EdgeCollider2D ec = GetComponent<EdgeCollider2D>();
			while(ec != null) {
				Undo.DestroyObjectImmediate(ec);
				ec = GetComponent<EdgeCollider2D>();
			}
		} else {
#endif
			foreach (EdgeCollider2D ec in GetComponents<EdgeCollider2D>()) {
				Destroy(ec);
			}
#if UNITY_EDITOR
		}
#endif
		edgeColliders = null;
	}

	void DestroyEdgeFx(int fromIndex) {
		MapEdgeGfx[] edgesLeft = gameObject.GetComponentsInChildren<MapEdgeGfx>();
#if UNITY_EDITOR
		if (Application.isEditor) {
			// in the editor have to use DestroyImmediate, so delete from tail
			// and kep retrieving the list (cannot iterate when deleting)
			while (edgesLeft.Length > fromIndex) {
				Undo.DestroyObjectImmediate(edgesLeft[edgesLeft.Length-1].gameObject);
				edgesLeft = gameObject.GetComponentsInChildren<MapEdgeGfx>();
			}
		} else {
#endif
			for (int i = tempData.outlines.Count; i < edgesLeft.Length; ++i) {
				Destroy(edgesLeft[i].gameObject);
			}
#if UNITY_EDITOR
		}
#endif

	}

	void CreateEdges() {

		// Remove all former edge colliders
		DestroyEdgeColliders();

        CreateOutlines();

		MapEdgeGfx[] edgeGfxs = gameObject.GetComponentsInChildren<MapEdgeGfx>();

		for (int oi = 0; oi < tempData.outlines.Count; ++oi) {
        	List<int> outline = tempData.outlines[oi];
			float lineSquaredLen = 0f;
#if UNITY_EDITOR
			EdgeCollider2D ec = Application.isEditor ? Undo.AddComponent<EdgeCollider2D>(gameObject) : gameObject.AddComponent<EdgeCollider2D>();
#else
			EdgeCollider2D ec = gameObject.AddComponent<EdgeCollider2D>();
#endif
			// Bleh, EdgeCollider2D and LineRenderer take 2D & 3D points & can't re-use
			Vector2[] points2d = new Vector2[outline.Count];
			Vector3[] points3d = new Vector3[outline.Count];
            for (int i = 0; i < outline.Count; i++) {
				points2d[i] = tempData.vertices[outline[i]];
				points3d[i] = tempData.vertices[outline[i]];
				if (i > 0) {
					lineSquaredLen += (points2d[i] - points2d[i-1]).SqrMagnitude();
				}
            }
			ec.points = points2d;

			// Also create / reuse child objects for line rendering
			// Separate children because each LineRenderer needs its own
			// GameObject apparently
			if (oi < edgeGfxs.Length) {
				// re-use
				edgeGfxs[oi].SetEdges(points3d, edgeMaterial, edgeWidth, lineSquaredLen);
			} else {
				GameObject child = new GameObject("EdgeGfx");
				child.transform.SetParent(gameObject.transform);
#if UNITY_EDITOR
				MapEdgeGfx e = Application.isEditor ? Undo.AddComponent<MapEdgeGfx>(child) : child.AddComponent<MapEdgeGfx>();
#else
				MapEdgeGfx e = child.AddComponent<MapEdgeGfx>();
#endif
				e.SetEdges(points3d, edgeMaterial, edgeWidth, lineSquaredLen);
			}
        }

		// Delete any game objects left over
		DestroyEdgeFx(tempData.outlines.Count);
	}

	void TriangulateGrid(SquareGrid squareGrid) {
		switch(triangulateStyle) {
			case TriangulateMode.Rows:
				TriangulateInRows(squareGrid);
				break;
			case TriangulateMode.Columns:
				TriangulateInColumns(squareGrid);
				break;
			case TriangulateMode.Spiral:
				TriangulateInSpiral(squareGrid);
				break;
		}
	}

	void TriangulateInSpiral(SquareGrid squareGrid) {
		int maxx = squareGrid.squares.GetLength(0) - 1;
		int maxy = squareGrid.squares.GetLength(1) - 1;
		int minx = 0;
		int miny = 0;
		int x = 0;
		int y = 0;
		int xdirection = 1;
		int ydirection = 0;
		int xfill = 1;
		int yfill = 1;
		int squaresLeft = (maxx+1) * (maxy+1);
		while (squaresLeft > 0) {
			TriangulateSquare(squareGrid, x, y, xfill, yfill);

			int newx = x + xdirection;
			int newy = y + ydirection;
			if (newx > maxx || newy > maxy || newx < minx || newy < miny) {
				// Hit an edge, always turn left
				if (xdirection == 1) {
					xdirection = 0;
					ydirection = 1;
					miny = y + 1;
					xfill = -1;
				} else if (ydirection == 1) {
					ydirection = 0;
					xdirection = -1;
					maxx = x - 1;
					yfill = -1;
				} else if (xdirection == -1) {
					xdirection = 0;
					ydirection = -1;
					maxy = y - 1;
					xfill = 1;
				} else if (ydirection == -1) {
					ydirection = 0;
					xdirection = 1;
					minx = x + 1;
					yfill = 1;
				}


			}
			x += xdirection;
			y += ydirection;
			--squaresLeft;

		}


	}

	void TriangulateInRows(SquareGrid squareGrid) {
		int width = squareGrid.squares.GetLength(0);
		int height = squareGrid.squares.GetLength(1);
		for (int y = 0; y < height; y++) {
			for (int x = 0; x < width; x++) {
				TriangulateSquare(squareGrid, x, y);
			}
		}
	}
	void TriangulateInColumns(SquareGrid squareGrid) {
		int width = squareGrid.squares.GetLength(0);
		int height = squareGrid.squares.GetLength(1);
		for (int x = 0; x < width; x++) {
			for (int y = 0; y < height; y++) {
				TriangulateSquare(squareGrid, x, y);
			}
		}
	}

	void TriangulateSolidSquare(Square s, SquareGrid squareGrid,
		int xstart, int ystart, int xdirection, int ydirection) {

		// Nothing to do if this has already been merged
		if (s.convertedToMesh)
			return;

		// Merge solid squares into a rectangle
		bool goVert = true;
		bool goHorz = true;
		int width = squareGrid.squares.GetLength(0);
		int height = squareGrid.squares.GetLength(1);
		int xcount = xdirection; // counts can be +ve or -ve
		int ycount = ydirection;
		while (goHorz || goVert) {
			// Right; must be able to merge whole column
			if (goHorz) {
				if (xstart+xcount >= width || xstart+xcount < 0) {
					goHorz = false;
				} else {
					for (int y = ystart; y != ystart+ycount; y += ydirection) {
						Square squareNext = squareGrid.squares[xstart+xcount, y];
						goHorz = squareNext.IsSolid() && !squareNext.convertedToMesh;
						if (!goHorz)
							break;
					}
				}
			}
			// Up; must be able to merge whole row
			if (goVert) {
				if (ystart+ycount >= height || ystart+ycount < 0) {
					goVert = false;
				} else {
					for (int x = xstart; x != xstart+xcount; x += xdirection) {
						Square squareNext = squareGrid.squares[x, ystart+ycount];
						goVert = squareNext.IsSolid() && !squareNext.convertedToMesh;
						if (!goVert)
							break;
					}
				}
			}
			// Corner - if this fails we must pick either up OR right
			if (goHorz && goVert) {
				Square squareCorner = squareGrid.squares[xstart+xcount, ystart+ycount];
				if (!squareCorner.IsSolid() || squareCorner.convertedToMesh) {
					// Randomly pick up or right to mix it up
					goHorz = false;
				}
			}

			if (goVert)
				ycount += ydirection;
			if (goHorz)
				xcount += xdirection;

			// To avoid making lots of thin regions, stop when ratio gets too much
			if (!goVert || !goHorz) {
				float ratio = Mathf.Abs((float)xcount / (float)ycount);
				if (ratio > triangulateMaxRatio || ratio < 1f/triangulateMaxRatio) {
					goVert = goHorz = false;
				}
			}
		}

		// Now we generate one quad from xtart,ystart - xstart+xcount-1,ystart+ycount-1
		Square squareBottomLeft = s;
		Square squareTopLeft = squareGrid.squares[xstart, ystart+ycount-ydirection];
		Square squareTopRight = squareGrid.squares[xstart+xcount-xdirection, ystart+ycount-ydirection];
		Square squareBottomRight = squareGrid.squares[xstart+xcount-xdirection, ystart];
		if (xdirection < 0) {
			Square t = squareTopLeft;
			squareTopLeft = squareTopRight;
			squareTopRight = t;
			t = squareBottomLeft;
			squareBottomLeft = squareBottomRight;
			squareBottomRight = t;
		}
		if (ydirection < 0) {
			Square t = squareTopLeft;
			squareTopLeft = squareBottomLeft;
			squareBottomLeft = t;
			t = squareTopRight;
			squareTopRight = squareBottomRight;
			squareBottomRight = t;

		}
		MeshFromPoints(squareTopLeft.topLeft, squareTopRight.topRight,
			squareBottomRight.bottomRight, squareBottomLeft.bottomLeft);

		// And mark these as generated
		// We can't do that while iterating above because we have to know the
		// complete picture before confirming they can all be merged
		for (int y = ystart; y != ystart+ycount; y += ydirection) {
			for (int x = xstart; x != xstart+xcount; x += xdirection) {
				Square sq = squareGrid.squares[x,y];
				sq.convertedToMesh = true;
			}
		}

	}

	void TriangulateSquare(SquareGrid squareGrid,
		int x, int y, int xdirection = 1, int ydirection = 1) {

		Square s = squareGrid.squares[x,y];
		if (s.IsSolid()) {
			TriangulateSolidSquare(s, squareGrid, x, y, xdirection, ydirection);
		} else {
			TriangulateSquare(s);
		}
		s.convertedToMesh = true;

	}
	void TriangulateSquare(Square square) {
		// This is the 16 marching squares cases
		// ABCD are stored in big endian order so A=1000 (8), B=0100 (4) etc
		switch(square.variant) {
		case 0:
			// No points active
			break;
		// 1 point active: single triangle in the corner
		case 1: // D only
			MeshFromPoints(square.centreLeft, square.centreBottom, square.bottomLeft);
			EdgeFromPoints(square.centreLeft, square.centreBottom);
			break;
		case 2: // C only
			MeshFromPoints(square.centreBottom, square.centreRight, square.bottomRight);
			EdgeFromPoints(square.centreBottom, square.centreRight);
			break;
		case 4: // B only
            MeshFromPoints(square.centreRight, square.centreTop, square.topRight);
			EdgeFromPoints(square.centreRight, square.centreTop);
			break;
		case 8: // A only
			MeshFromPoints(square.centreTop, square.centreLeft, square.topLeft);
			EdgeFromPoints(square.centreTop, square.centreLeft);
			break;

        // 2 points: quad if on same side, diamond if opposite
        case 3: // C & D - quad
            MeshFromPoints(square.centreRight, square.bottomRight, square.bottomLeft, square.centreLeft);
			EdgeFromPoints(square.centreLeft, square.centreRight);
            break;
        case 6: // B & C - quad
            MeshFromPoints(square.centreTop, square.topRight, square.bottomRight, square.centreBottom);
            EdgeFromPoints(square.centreBottom, square.centreTop);
            break;
        case 9: // A & D - quad
            MeshFromPoints(square.topLeft, square.centreTop, square.centreBottom, square.bottomLeft);
            EdgeFromPoints(square.centreTop, square.centreBottom);
            break;
        case 12: // A & B - quad
            MeshFromPoints(square.topLeft, square.topRight, square.centreRight, square.centreLeft);
            EdgeFromPoints(square.centreRight, square.centreLeft);
            break;
        case 5: // B & D - diamond
            MeshFromPoints(square.centreTop, square.topRight, square.centreRight, square.centreBottom, square.bottomLeft, square.centreLeft);
			// 2 separate edges (not joined)
            EdgeFromPoints(square.centreRight, square.centreBottom);
            EdgeFromPoints(square.centreLeft, square.centreTop);
            break;
        case 10: // A & C - diamond
            MeshFromPoints(square.topLeft, square.centreTop, square.centreRight, square.bottomRight, square.centreBottom, square.centreLeft);
			// 2 separate edges (not joined)
            EdgeFromPoints(square.centreTop, square.centreRight);
            EdgeFromPoints(square.centreBottom, square.centreLeft);
            break;

        // 3 points:
        case 7: // B & C & D
            MeshFromPoints(square.centreTop, square.topRight, square.bottomRight, square.bottomLeft, square.centreLeft);
			EdgeFromPoints(square.centreLeft, square.centreTop);
            break;
        case 11: // A & C & D
            MeshFromPoints(square.topLeft, square.centreTop, square.centreRight, square.bottomRight, square.bottomLeft);
			EdgeFromPoints(square.centreTop, square.centreRight);
            break;
        case 13: // A & B & D
            MeshFromPoints(square.topLeft, square.topRight, square.centreRight, square.centreBottom, square.bottomLeft);
			EdgeFromPoints(square.centreRight, square.centreBottom);
            break;
        case 14: // A & B & C
            MeshFromPoints(square.topLeft, square.topRight, square.bottomRight, square.centreBottom, square.centreLeft);
			EdgeFromPoints(square.centreBottom, square.centreLeft);
            break;

        // 4 point:
        case 15: // All points, solid quad over whole square
			// Mesh is created in TriangulateSolidSquare instead (merged neighbours)
			// None of these are ever edges
			// So no need to call EdgeFromPoints
            break;
		}
	}

	/// Generates a mesh from n points, listed clockwise (polygon not tris)
	void MeshFromPoints(params Node[] points) {
		AssignVertices(points);

		// points.Length - 2 triangles
		for (int i = 0; i < points.Length-2; i++) {
			// Triangle is always the first point, to the last 2 points
			CreateTriangle(points[0], points[i+1], points[i+2]);
		}
	}
	/// Registers an edge which is used to build an outline later
	void EdgeFromPoints(params Node[] points) {

		for (int i = 1; i < points.Length; ++i) {
			Node point = points[i];
			Node prevPoint = points[i-1];
			tempData.vertexEdgeNeighbourMap.Add(prevPoint.vertexIndex, point.vertexIndex);

			// Mark all points as part of an edge so need outline
			tempData.verticesOutlineDone[point.vertexIndex] = false;
			if (i == 1) { // because we never do index 0
				tempData.verticesOutlineDone[prevPoint.vertexIndex] = false;
			}
		}
	}

	void AssignVertices(Node[] points) {

		foreach (Node n in points) {
			if (n.vertexIndex == -1)
				n.vertexIndex = tempData.vertices.Count;
				tempData.vertices.Add(n.position);
				// Mark all vertices as done, only set them as needing doing when
				// marked part of an edge in EdgeFromPoints
				tempData.verticesOutlineDone.Add(true);
		}
	}

	void CreateTriangle(Node a, Node b, Node c) {
		tempData.triangles.Add(a.vertexIndex);
		tempData.triangles.Add(b.vertexIndex);
		tempData.triangles.Add(c.vertexIndex);
	}

	void CreateOutlines() {

		// 1. Find a vertex where verticesOutlineDone = false
		// 2. Walk vertexEdgeNeighbourMap until either no neighbour or cycle
		// 3. Return to 1 or exit if none

		tempData.outlines = new List<List<int>>();

		int outlineStart = GetOutlineStart(0);
		while (outlineStart != -1) {
			List<int> outline = TraceOutline(outlineStart);
			if (outline.Count > 1) {
				tempData.outlines.Add(outline);
			}
			outlineStart = GetOutlineStart(outlineStart+1);
		}
	}

	/// Find a vertex which is not part of an outline already, or -1 if none
	/// <param name="searchStart">Vertex index to start searching from</param>
	int GetOutlineStart(int searchStart) {
		for (int i = searchStart; i < tempData.verticesOutlineDone.Count; ++i) {
			if (tempData.verticesOutlineDone[i] == false) {
				return i;
			}
		}
		return -1;
	}

	/// Starting at a vertex index (which must be an edge), trace an outline
	/// Outlines may be closed or open; if closed the last index will equal the first
	/// Vertices are marked as visited when processed
	List<int> TraceOutline(int start) {
		List<int> ret = new List<int>();
		ret.Add(start);
		tempData.verticesOutlineDone[start] = true;
		int current = start;
		int next = -1;
		while (tempData.vertexEdgeNeighbourMap.TryGetValue(current, out next)) {
			ret.Add(next);
			tempData.verticesOutlineDone[next] = true;
			if (next == start) {
				// Full cycle, stop here
				break;
			}
			current = next;
		}
		return ret;
	}


	/// <summary>
	/// Callback to draw gizmos that are pickable and always drawn.
	/// </summary>
	// void OnDrawGizmos()
	// {
	// 	if (squareGrid != null) {
	// 		for (int x = 0; x < squareGrid.squares.GetLength(0); x++) {
	// 			for (int y = 0; y < squareGrid.squares.GetLength(1); y++) {
	// 				// Avoid overdraw since squares share nodes
	// 				if (x == 0 && y == 0)
	// 					DrawControlNodeGizmo(squareGrid.squares[x,y].topLeft);
	// 				if (y == 0)
	// 					DrawControlNodeGizmo(squareGrid.squares[x,y].topRight);
	// 				if (x == 0)
	// 					DrawControlNodeGizmo(squareGrid.squares[x,y].bottomLeft);
	// 				DrawControlNodeGizmo(squareGrid.squares[x,y].bottomRight);

	// 				if (y == 0)
	// 					DrawNodeGizmo(squareGrid.squares[x,y].centreTop);
	// 				DrawNodeGizmo(squareGrid.squares[x,y].centreRight);
	// 				DrawNodeGizmo(squareGrid.squares[x,y].centreBottom);
	// 				if (x == 0)
	// 					DrawNodeGizmo(squareGrid.squares[x,y].centreLeft);
	// 			}
	// 		}
	// 	}
	// }

	void DrawControlNodeGizmo(ControlNode n) {
		Gizmos.color = Color.Lerp(Color.black, Color.white, (float)n.value/255f);
		Gizmos.DrawCube(n.position, Vector3.one * 0.4f);
	}
	void DrawNodeGizmo(Node n) {
		// Gizmos.color = Color.grey;
		// Gizmos.DrawCube(n.position, Vector3.one * 0.15f);
	}

	public class SquareGrid {
		public Square[,] squares;

		public SquareGrid(byte[,] map, float squareSize) {
			int nodeCountX = map.GetLength(0);
			int nodeCountY = map.GetLength(1);
			float halfSquareSize = squareSize * 0.5f;
			float mapXOffset = -squareSize * nodeCountX * 0.5f + halfSquareSize;
			float mapYOffset = -squareSize * nodeCountY * 0.5f + halfSquareSize;

			ControlNode[,] nodes = new ControlNode[nodeCountX, nodeCountY];
			for (int x = 0; x < nodeCountX; x++) {
				for (int y = 0; y < nodeCountY; y++) {
					Vector3 pos = new Vector3(x * squareSize + mapXOffset, y * squareSize + mapYOffset, 0);
					nodes[x,y] = new ControlNode(pos, map[x,y], squareSize);
				}
			}

			squares = new Square[nodeCountX-1, nodeCountY-1];
			for (int x = 0; x < nodeCountX-1; x++) {
				for (int y = 0; y < nodeCountY-1; y++) {
					squares[x,y] = new Square(nodes[x,y+1], nodes[x+1,y+1], nodes[x+1,y], nodes[x,y]);
				}
			}


		}
	}

	public class Square {
		public ControlNode topLeft, topRight, bottomRight, bottomLeft;
		public Node centreTop, centreRight, centreBottom, centreLeft;
		// The type of square this is, 16 variants
		public int variant;
		// Whether this square has been turned into mesh data yet
		// Used when consolidating complete squares
		public bool convertedToMesh;

		public Square(ControlNode _tl, ControlNode _tr, ControlNode _br, ControlNode _bl) {
			topLeft = _tl;
			topRight = _tr;
			bottomRight = _br;
			bottomLeft = _bl;

			centreTop = topLeft.right;
			centreRight = bottomRight.above;
			centreBottom = bottomLeft.right;
			centreLeft = bottomLeft.above;

			CalculateVariant();

			// Move centre points to non-centre based on weights
			RepositionCentreNodeByWeight(topLeft, topRight, centreTop);
			RepositionCentreNodeByWeight(bottomLeft, topLeft, centreLeft);
			RepositionCentreNodeByWeight(bottomRight, topRight, centreRight);
			RepositionCentreNodeByWeight(bottomLeft, bottomRight, centreBottom);

		}

		void RepositionCentreNodeByWeight(ControlNode n1, ControlNode n2, Node c) {
			float p = GetCentreNodeWeightedPos(n1.value, n2.value);
			c.position = Vector3.Lerp(n1.position, n2.position, p);
		}

		/// Given the values of 2 corner nodes, get the parametric position of
		/// the centre node between them, return true if non-standard
		float GetCentreNodeWeightedPos(byte v1, byte v2) {

			float f1 = (float)v1/255f;
			float f2 = (float)v2/255f;
			float diff = f2 - f1;
			if (Mathf.Abs(diff) < Mathf.Epsilon) {
				return 1.0f;
			}
			return (0.5f - f1)/diff;
		}

		void CalculateVariant() {
			// One bit per active corner, clockwise from top-left
			variant = 0;
			if (topLeft.value >= Map.SolidThreshold)
				variant +=  1 << 3;
			if (topRight.value >= Map.SolidThreshold)
				variant +=  1 << 2;
			if (bottomRight.value >= Map.SolidThreshold)
				variant +=  1 << 1;
			if (bottomLeft.value >= Map.SolidThreshold)
				variant +=  1;
		}

		// Returns whether this square is a completely solid square ie variant 15
		public bool IsSolid() {
			return variant == 15;
		}
	}

	// Node is a point on the square
	public class Node {
		public Vector3 position;
		public int vertexIndex = -1;

		public Node(Vector3 pos) {
			position = pos;
		}
	}

	// ControlNode is one of the 4 points from the map which are either walls or space
	public class ControlNode : Node {
		// active = wall if true, space if false
		public byte value;
		// The Nodes which are owned by this node (for allocation only)
		public Node above, right;

		public ControlNode(Vector3 _pos, byte _val, float _squareSize) : base(_pos) {
			value = _val;
			above = new Node(position + Vector3.up * _squareSize * 0.5f);
			right = new Node(position + Vector3.right * _squareSize * 0.5f);
		}
	}

	// Triangle is used to store adjacency information for a triangle
	public class Triangle {
		public int[] v = new int[3];

		public Triangle(int _v1, int _v2, int _v3) {
			v[0] = _v1;
			v[1] = _v2;
			v[2] = _v3;
		}

		public bool Contains(int index) {
			for (int i = 0; i < 3; i++) {
				if (v[i] == index)
					return true;
			}
			return false;
		}
	}

	/// Return lists of points making up a flat floor area
	/// <param name="numpoints">Minimum number of points in the flat area. Larger areas may be returned</param>
	public List<List<Vector2>> GetFloorSegments(int numpoints) {
		// Because outlines are in temp data, we may not have them when
		// running from serialised data. But the edge colliders are basically the
		// same data, so use those
		EdgeCollider2D[] edgeColl = GetEdgeColliders();

		// Outlines are always constructed clockwise from the solid side
		// So we want a sequence of points with the same Y value where each X
		// value is greater than the previous one (floor rather than ceiling)
		List<List<Vector2>> ret = new List<List<Vector2>>();
		for (int o = 0; o < edgeColl.Length; ++o) {
			List<Vector2> sequence = new List<Vector2>(numpoints);
			Vector2[] outline = edgeColl[o].points;
			for (int i = 1; i < outline.Length; ++i) {
				Vector2 prev = outline[i-1];
				Vector2 curr = outline[i];
				if (Mathf.Approximately(prev.y, curr.y) &&
				curr.x > prev.x) { // floor not ceiling (clockwise from solid)
					if (sequence.Count == 0) {
						sequence.Add(prev); // start of the sequence
					}
					sequence.Add(curr);
				} else {
					// sequence broken
					if (sequence.Count >= numpoints) {
						ret.Add(sequence);
						sequence = new List<Vector2>(numpoints);
					} else {
						sequence.Clear();
					}
				}
			}
			if (sequence.Count >= numpoints) {
				ret.Add(sequence);
			}
		}
		return ret;
	}
}
