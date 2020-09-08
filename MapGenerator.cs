using UnityEngine;
using System.Collections.Generic;
using System;

public class MapGenerator : MonoBehaviour, IMapSource {

	private int width;
	private int height;

	public bool useRandomSeed = false;
	public string seed;
	[Range(0,100)]
	public int randomFillPercent = 45;

	public int smoothing = 5;
	public int passageWidth = 2;
	public bool upscaleFilter = false;

	public int hiWallThreshold = 4;
	public int lowWallThreshold = 4;

	byte[,] map;
	int nextRoomSet = 0;

	// A single square inside the map
	public struct Tile {
		public int x, y;

		public Tile(int _x, int _y) {
			x = _x;
			y = _y;
		}

		public float SquaredDistance(Tile otherTile) {
			return Mathf.Pow(x - otherTile.x, 2) + Mathf.Pow(y - otherTile.y, 2);
		}
	}

	// A series of empty tiles that makes up a room
	public class Room {
		public List<Tile> roomTiles;
		public List<Tile> edgeTiles; // empty tiles at the edge
		public int size;
		public List<Room> connectedRooms;
		private int roomSet = -1; // rooms connected to each other in a set have same index§

		public int RoomSet {
			get { return roomSet; }
			set {
				if (roomSet != value && roomSet != -1) {
					// This room used to be in a different set
					// Must tell all connected rooms about set change too
					roomSet = value;
					for (int i = 0; i < connectedRooms.Count; ++i) {
						connectedRooms[i].RoomSet = value;
					}

				} else {
					roomSet = value;
				}

			}
		}

		// default constructor, can check size = 0 for empty
		public Room() {
		}

		// normal constructor
		public Room(List<Tile> tiles, byte[,] map) {
			roomTiles = tiles;
			edgeTiles = new List<Tile>();
			connectedRooms = new List<Room>();
			size = tiles.Count;
			int width = map.GetLength(0);
			int height = map.GetLength(1);
			foreach(Tile t in tiles) {
				// Check neighbours to find edges
				for (int x = t.x-1; x < t.x+1; x++) {
					for (int y = t.y-1; y < t.y+1; y++) {
						if ((x == t.x || y == t.y) && // Only check 4 directions, not diagonals
						x >= 0 && y >= 0 && x < width && y < height && // bounds check since we can have open edges
						map[x,y] != Map.Empty) {
							edgeTiles.Add(t);
						}
					}
				}
			}
		}

		public static void ConnectRooms(Room roomA, Room roomB, int roomSet) {
            roomA.connectedRooms.Add (roomB);
            roomB.connectedRooms.Add (roomA);
			roomA.RoomSet = roomSet;
			roomB.RoomSet = roomSet;
        }

		// Are rooms directly connected?
        public bool IsConnected(Room otherRoom) {
            return connectedRooms.Contains(otherRoom);
        }

		// Are rooms directly or indirectly connected?
		public bool IsInSameRoomSet(Room otherRoom) {
			return RoomSet != -1 && otherRoom.RoomSet != -1 && RoomSet == otherRoom.RoomSet;
		}

	}

	void GenerateMap() {
		map = new byte[width, height];
		nextRoomSet = 0;
		RandomFillMap();
		SmoothMap();
		DetectRegions();
		if (upscaleFilter) {
			map = UpscaleMap(map);
		}
	}

	void RandomFillMap() {
		if (useRandomSeed) {
			seed = System.DateTime.Now.Millisecond.ToString();
		}
		System.Random rnd = new System.Random(seed.GetHashCode());
		for (int x = 0; x < width; x++) {
			for (int y = 0; y < height; y++) {
				// Always have solid walls except top
				if (IsAlwaysSolid(x, y)) {
					map[x,y] = Map.Solid;
				} else if (IsAlwaysOpen(x, y)) {
					map[x,y] = Map.Empty;
				} else {
					map[x,y] = (rnd.Next(0, 100) < randomFillPercent) ? Map.Solid : Map.Empty;
				}
			}

		}
	}

	bool IsAlwaysSolid(int x, int y) {
		return 	x == 0 // left edge
			|| y == 0 // bottom edge
			|| x == width-1 // right edge
			|| y == height-1 // top edge
			;
	}

	bool IsAlwaysOpen(int x, int y) {
		return false;
		//return y == height-1; // open top
	}

	void SmoothMap() {
		for (int i = 0; i < smoothing; i++) {
			SmoothMapPass();
		}
	}

	void SmoothMapPass() {
		for (int x = 1; x < width-1; x++) {
			for (int y = 1; y < height-1; y++) {
				int neighbours = GetSurroundingWallCount(x,y);
				// Stablise at a value, flip if above/below
				if (neighbours > hiWallThreshold) {
					map[x,y] = Map.Solid;
				} else if (neighbours < lowWallThreshold) {
					map[x,y] = Map.Empty;
				}
			}
		}

	}

	int GetSurroundingWallCount(int x, int y) {
		int wallCount = 0;

		for (int nx = x-1; nx <= x+1; ++nx) {
			for (int ny = y-1; ny <= y+1; ++ny) {
				if (nx < 0 ||
					nx >= width || ny >= height) {
					// Always solid walls at/beyond boundary
					wallCount++;
				} else if (!(nx == x && ny == y) && map[nx,ny] != Map.Empty) {
					// Only count not self
					wallCount++;
				}
			}
		}
		return wallCount;
	}

	bool IsInMapRange(int x, int y) {
		return x >= 0 && y >= 0 &&
			x < width && y < height;
	}

	void DetectRegions() {
		List<List<Tile>> wallRegions = new List<List<Tile>>();
		List<List<Tile>> roomRegions = new List<List<Tile>>();
		bool[,] mapDone = new bool[width,height];
		for (int x = 0; x < width; ++x) {
			for (int y = 0; y < height; ++y) {
				if (!mapDone[x,y]) {
					List<List<Tile>> destList = map[x,y] != Map.Empty ? wallRegions : roomRegions;
					List<Tile> newRegion = GetRegionTiles(x, y, mapDone);
					destList.Add(newRegion);
					foreach (Tile t in newRegion) {
						mapDone[t.x,t.y] = true;
					}
				}
			}
		}

		EliminateSmallRegions(wallRegions, 10);
		List<Room> remainingRooms = new List<Room>();
		EliminateSmallRegions(roomRegions, 30, passedTiles => {
			remainingRooms.Add(new Room(passedTiles, map));
		});

		ConnectAllRooms(remainingRooms);

	}

	void ConnectAllRooms(List<Room> rooms) {
		// First pass will ensure all rooms are connected to something
		ConnectRooms(rooms, -1);

		int maxRoomSet = nextRoomSet - 1;
		while (maxRoomSet > 0) {
			// more than one island of connected rooms
			// Join them up, must iterate from 0 to ensure total connection
			for (int setIdx = 0; setIdx < nextRoomSet; ++setIdx) {
				ConnectRooms(rooms, setIdx);
			}

			// When rooms join a set they get the lowest set number so when done
			// all rooms should be in set 0
			// Not guaranteed to happen on first pass depending on how many
			// sub-islands there are
			maxRoomSet = 0;
			for (int i = 0; i < rooms.Count; ++i) {
				maxRoomSet = Math.Max(rooms[i].RoomSet, maxRoomSet);
			}
		}
	}

	void EliminateSmallRegions(List<List<Tile>> regions, int minSize, Action<List<Tile>> passFunc = null) {

		foreach (List<Tile> r in regions) {
			if (r.Count == 0)
				continue;

			if (r.Count < minSize) {
				byte newVal = map[r[0].x, r[0].y] == Map.Empty ? Map.Solid : Map.Empty;
				foreach(Tile t in r) {
					map[t.x,t.y] = newVal;
				}
			} else if (passFunc != null) {
				passFunc(r);
			}
		}
	}

	// Connect rooms which have either no set, or are in different sets
	// @param withSetIdx If not -1, only tries to connect rooms with this set number
	void ConnectRooms(List<Room> rooms, int withSetIdx) {
		float bestDistance = width * height;
		bool possibleConnectionFound = false;
		// Copies of best candidates
		Tile bestTile1 = new Tile();
		Tile bestTile2 = new Tile();
		Room bestRoom1 = new Room();
		Room bestRoom2 = new Room();

		foreach(Room roomA in rooms) {
			// If we're only connecting rooms from a given set, skip if not that set
			if (withSetIdx != -1 && roomA.RoomSet != withSetIdx)
				continue;

			if (withSetIdx == -1) {
				// This is the initial pass where we're trying to connect each
				// room to one other
				possibleConnectionFound = false;
				bestDistance = width * height;
			}

			foreach(Room roomB in rooms) {

				if (roomA == roomB) {
					continue;
				}

				if (roomA.IsInSameRoomSet(roomB)) {
					// These two rooms are already connected directly/indirectly
					if (withSetIdx == -1) {
						// Don't need to connect anything in first pass
						possibleConnectionFound = false;
						break;
					}
					// In set pass, need to keep looking
					continue;
				}

				for (int i = 0; i < roomA.roomTiles.Count; ++i) {
					for (int j = 0; j < roomB.roomTiles.Count; ++j) {
						Tile roomATile = roomA.roomTiles[i];
						Tile roomBTile = roomB.roomTiles[j];

						float dist = roomATile.SquaredDistance(roomBTile);
						if (dist < bestDistance || !possibleConnectionFound) {
							bestDistance = dist;
							possibleConnectionFound = true;
							bestRoom1 = roomA;
							bestTile1 = roomATile;
							bestTile2 = roomBTile;
							bestRoom2 = roomB;
						}
					}
				}
			}

			// In room pass, connect each room
			if (withSetIdx == -1 && possibleConnectionFound) {
				CreatePassage(roomA, bestTile1, bestRoom2, bestTile2);
			}
		}

		// In set pass, connect each set
		if (withSetIdx != -1 && possibleConnectionFound) {
			CreatePassage(bestRoom1, bestTile1, bestRoom2, bestTile2);
		}
	}

	void CreatePassage(Room fromRoom, Tile fromTile, Room toRoom, Tile toTile) {
		int roomSet = -1;
		if (fromRoom.RoomSet == -1 && toRoom.RoomSet == -1) {
			roomSet = nextRoomSet++;
		} else if (fromRoom.RoomSet != -1 && toRoom.RoomSet != -1) {
			// Always pick the lower of the 2 room sets when joining so eventually
			// every room is in set 0
			roomSet = Math.Min(fromRoom.RoomSet, toRoom.RoomSet);
		} else {
			roomSet = (fromRoom.RoomSet != -1) ? fromRoom.RoomSet : toRoom.RoomSet;
		}
		Room.ConnectRooms(fromRoom, toRoom, roomSet);

		// Vector3 v1 = TileWorldPosition(fromTile);
		// Vector3 v2 = TileWorldPosition(toTile);
		// Debug.DrawLine(v1, v2, Color.green, 5, false);

		List<IntVector2> line = IntVector2.GetLine(
			new IntVector2(fromTile.x, fromTile.y),
			new IntVector2(toTile.x, toTile.y));

		foreach(IntVector2 pos in line) {
			DrawCircle(pos.x, pos.y, passageWidth, Map.Empty);
		}
	}

	void DrawCircle(int posx, int posy, int radius, byte val) {
		int sqradius = radius*radius;
		for (int x = -radius; x <= radius; ++x) {
			for (int y = -radius; y <= radius; ++y) {
				if (x*x + y*y <= sqradius) {
					map[posx+x, posy+y] = val;
				}
			}
		}
	}

	Vector3 TileWorldPosition(Tile t) {
		return new Vector3(-width*0.5f+0.5f+t.x, -height*0.5f+0.5f+t.y, -2f);
	}


	// Get all tiles in a region starting at a map coordinate using flood fill
	// Returns either entirely empty region or entirely filled region
	List<Tile> GetRegionTiles(int startX, int startY, bool[,] mapDone) {

		List<Tile> ret = new List<Tile>();
		mapDone[startX,startY] = true;
		int tileType = map[startX, startY];
		// Flood fill outwards using queue
		Queue<Tile> queue = new Queue<Tile>();
		queue.Enqueue(new Tile(startX, startY));
		while (queue.Count > 0) {
			Tile t = queue.Dequeue();
			ret.Add(t);
			// Add 4 adjacent tiles (not diagonals) if unvisited
			for (int x = t.x-1; x <= t.x+1; ++x) {
				for (int y = t.y-1; y <= t.y+1; ++y) {
					if ((x == t.x || y == t.y) && IsInMapRange(x, y) &&
						map[x,y] == tileType && !mapDone[x,y]) {
						queue.Enqueue(new Tile(x,y));
						mapDone[x, y] = true;
					}
				}
			}
		}
		return ret;
	}

	/// Upscale incoming map to 2x in each dimension, gaussian filtering the result
	byte[,] UpscaleMap(byte[,] inmap) {
		// Standard 3x3 gaussian kernel
		float[,] kernel = new float[,] {
			{0.077847f, 	0.123317f,	0.077847f},
			{0.123317f,	0.195346f,	0.123317f},
			{0.077847f,	0.123317f,	0.077847f }};

		int inwidth = inmap.GetLength(0);
		int inheight = inmap.GetLength(1);
		int newwidth = inwidth * 2;
		int newheight = inheight * 2;
		byte[,] newmap = new byte[newwidth, newheight];

		for (int x = 0; x < newwidth; ++x) {
			for (int y = 0; y < newheight; ++y) {
				// This is an output pixel
				// Loop over the gaussian kernel
				float accum = 0f;
				for (int dx = -1; dx <= 1; ++ dx) {
					for (int dy = -1; dy <= 1; ++ dy) {
						int samplex = MathUtil.Clamp((x + dx) / 2, 0, inwidth-1);
						int sampley = MathUtil.Clamp((y + dy) / 2, 0, inheight-1);
						float origValue = (float)inmap[samplex, sampley];
						float weight = kernel[1+dx, 1+dy];
						accum += origValue * weight;
					}
				}
				newmap[x,y] = (byte)accum;
			}
		}
		return newmap;
	}

	#region IMapSource
	public byte[,] GetMapData(int desiredWidth, int desiredHeight, bool reload) {
		if (reload || map == null ||
		  desiredWidth != width || desiredHeight != height) {
			width = desiredWidth;
			height = desiredHeight;
			GenerateMap();
		}
		return map;
	}
	#endregion

}
