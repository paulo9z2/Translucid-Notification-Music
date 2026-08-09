using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Media.Control;

namespace Translucid.Core;

/// <summary>Snapshoot of what the system media session is playing.</summary>
public sealed class MediaUpdate
{
    public required string Title { get; init; }
    public required string Artist { get; init; }
    public required string Album { get; init; }
    public required string AppName { get; init; }
    public byte[]? Thumbnail { get; init; }
    public TimeSpan Position { get; init; }
    public TimeSpan Duration { get; init; }
    public bool IsPlaying { get; init; }
    public bool CanNext { get; init; }
    public bool CanPrevious { get; init; }
    public bool CanPlayPause { get; init; }

    public static MediaUpdate Idle { get; } = new()
    {
        Title = "Nada tocando",
        Artist = "",
        Album = "",
        AppName = "",
        Position = TimeSpan.Zero,
        Duration = TimeSpan.Zero,
    };
}

/// <summary>
/// Acompanha a sessão de mídia do sistema (SMTC) via
/// Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager.
/// Funciona com YouTube Music no navegador, Spotify, apps nativos, etc.
/// </summary>
public sealed class MediaTracker : IDisposable
{
    public event Action<MediaUpdate>? Updated;

    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task StartAsync()
    {
        _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        _manager.CurrentSessionChanged += OnCurrentSessionChanged;
        await AttachAsync(_manager.GetCurrentSession());
    }

    private async void OnCurrentSessionChanged(
        GlobalSystemMediaTransportControlsSessionManager sender,
        CurrentSessionChangedEventArgs args) =>
        await AttachAsync(sender.GetCurrentSession());

    private async Task AttachAsync(GlobalSystemMediaTransportControlsSession? session)
    {
        await _gate.WaitAsync();
        try
        {
            if (_session is not null)
            {
                _session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
                _session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
                _session.TimelinePropertiesChanged -= OnTimelinePropertiesChanged;
            }

            _session = session;

            if (_session is not null)
            {
                _session.MediaPropertiesChanged += OnMediaPropertiesChanged;
                _session.PlaybackInfoChanged += OnPlaybackInfoChanged;
                _session.TimelinePropertiesChanged += OnTimelinePropertiesChanged;
            }
        }
        finally
        {
            _gate.Release();
        }

        await PushAsync();
    }

    private async void OnMediaPropertiesChanged(
        GlobalSystemMediaTransportControlsSession sender,
        MediaPropertiesChangedEventArgs args) => await PushAsync();

    private async void OnPlaybackInfoChanged(
        GlobalSystemMediaTransportControlsSession sender,
        PlaybackInfoChangedEventArgs args) => await PushAsync();

    private async void OnTimelinePropertiesChanged(
        GlobalSystemMediaTransportControlsSession sender,
        TimelinePropertiesChangedEventArgs args) => await PushAsync();

    /// <summary>Re-collects the current snapshot and raises <see cref="Updated"/>.</summary>
    public async Task PushAsync()
    {
        var session = _session;
        if (session is null)
        {
            Updated?.Invoke(MediaUpdate.Idle);
            return;
        }

        try
        {
            var props = await session.TryGetMediaPropertiesAsync();
            var timeline = session.GetTimelineProperties();
            var playback = session.GetPlaybackInfo();
            var controls = playback.Controls;

            byte[]? thumbnail = null;
            if (props?.Thumbnail is not null)
            {
                try
                {
                    var stream = await props.Thumbnail.OpenReadAsync();
                    using var buffer = new MemoryStream();
                    using (var input = stream.AsStreamForRead())
                    {
                        await input.CopyToAsync(buffer);
                    }

                    thumbnail = buffer.ToArray();
                }
                catch
                {
                    thumbnail = null;
                }
            }

            var duration = timeline.EndTime - timeline.StartTime;

            Updated?.Invoke(new MediaUpdate
            {
                Title = props?.Title ?? string.Empty,
                Artist = props?.Artist ?? string.Empty,
                Album = props?.AlbumTitle ?? string.Empty,
                AppName = ResolveAppName(session.SourceAppUserModelId),
                Thumbnail = thumbnail,
                Position = timeline.Position,
                Duration = duration > TimeSpan.Zero ? duration : TimeSpan.Zero,
                IsPlaying = playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                CanNext = controls.IsNextEnabled,
                CanPrevious = controls.IsPreviousEnabled,
                CanPlayPause = controls.IsPlayEnabled || controls.IsPauseEnabled,
            });
        }
        catch
        {
            Updated?.Invoke(MediaUpdate.Idle);
        }
    }

    private static string ResolveAppName(string appUserModelId)
    {
        try
        {
            var info = Windows.ApplicationModel.AppInfo.GetFromAppUserModelId(appUserModelId);
            return info.DisplayInfo.DisplayName;
        }
        catch
        {
            return appUserModelId;
        }
    }

    public async Task TogglePlayPauseAsync()
    {
        if (_session is null) return;
        if (_session.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
            await _session.TryPauseAsync();
        else
            await _session.TryPlayAsync();
        await PushAsync();
    }

    public async Task NextAsync()
    {
        if (_session is null) return;
        await _session.TrySkipNextAsync();
        await PushAsync();
    }

    public async Task PreviousAsync()
    {
        if (_session is null) return;
        await _session.TrySkipPreviousAsync();
        await PushAsync();
    }

    public void Dispose()
    {
        if (_manager is not null)
        {
            _manager.CurrentSessionChanged -= OnCurrentSessionChanged;
        }

        if (_session is not null)
        {
            _session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
            _session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
            _session.TimelinePropertiesChanged -= OnTimelinePropertiesChanged;
        }

        _gate.Dispose();
    }
}