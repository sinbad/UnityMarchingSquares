

public interface IMapSource {

    /// Retrieve map data, which is a 2D array of 0-255 values, where 0 is
    /// empty and 255 is 100% solid
    /// <param name="desiredWidth">Input width if map source supports configurable width</param>
    /// <param name="desiredHeight">Input height if map source supports configurable height</param>
    /// <param name="reload">If true, reload/regenerate map data if already loaded</param>
    /// <returns>Array of 0-255 solidity values; check the dimensions, may be different to requested width/height</returns>
    byte[,] GetMapData(int desiredWidth, int desiredHeight, bool reload);

}