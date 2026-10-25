namespace NovaPlayer.Core.Presentation;

public sealed record ShellLayout(double NavigationWidth, bool CompactNavigation, bool ShowPlaylist, bool StackInformation)
{
    public static ShellLayout Calculate(double width, bool playlistRequested) =>
        new(width < 1050 ? 72 : 190, width < 1050, width >= 1200 && playlistRequested, width < 1200);
}
