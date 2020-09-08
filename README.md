# Unity Marching Squares Implementation

I pulled this marching squares implementation out of an old prototype Thrust-a-like game I made for Unity in 2017 
in order to share it, since I think it's quite generic and probably useful for others. It was built for Unity 2017 
but the code is pretty generic.

## Features:

* Given a 2D array of "densities"
  * Building a background mesh for the solid areas
  * Building line renderers for the edges
  * Building EdgeColliders

* Procedurally generating density maps where all areas are reachable

## Classes: 

* Map
  Holder for all the data structures for a single map.
* IMapSource
  An interface for providing a 2D array of density data to Map. This can be manually authored or generated.
* MeshGenerator
  Builds the background mesh for Map, builds colliders, and communicates with the edge generator
* MapEdgeGfx
  Builds line renderers for the edges of the solid areas
* MapGenerator
  Procedurally generates a map of density data which is always traversable, is an IMapSource
  
  
  ## How to use
  
   1. Create a GameObject in your level
   2. Add the Map and MeshGenerator components to it
   3. Add a MeshFilter component (this will hold the mesh)
   4. Add an IMapSource component, e.g. a MapGenerator for a procedural map
   5. Call Map.RefreshMap
   
 You'll want to tweak materials etc but everything is standard Unity.
   
 
  

