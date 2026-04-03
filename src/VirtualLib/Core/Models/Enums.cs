namespace VirtualLib.Core.Models;

public enum MediaType
{
    Movie,
    Episode,
    Music,
    Photo,
    Book,
    AudioBook
}

public enum ArtworkType
{
    Poster,    // Primary / cover
    Backdrop,  // Fanart / background
    Thumb,     // Landscape thumbnail
    Logo,      // ClearLogo
    Banner,    // Wide banner
    Disc,      // Disc / CD art
    Art        // ClearArt
}

public enum LibraryType
{
    Movies,
    TvShows,
    Music,
    Mixed,
    Books,
    Audiobooks,
    Photos,
    Unknown
}
