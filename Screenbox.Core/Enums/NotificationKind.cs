namespace Screenbox.Core.Enums;

public enum NotificationKind
{
    None,

    MusicLibraryAccessDenied,
    PicturesLibraryAccessDenied,
    VideosLibraryAccessDenied,

    InitializationFailed,
    FileOpenFailed,
    FolderAddFailed,
    MediaLoadFailed,
    SubtitleLoadFailed,
    FrameSaveFailed,
    DownloadFailed,

    FrameSaved,
    SubtitleAdded,
    PlaylistCreated,
    PlaylistDeleted,
    PlaylistRenamed,
    PlaylistItemsAdded,
    DownloadCompleted,

    ResumePosition,
}
