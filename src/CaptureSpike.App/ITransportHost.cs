namespace CaptureSpike.App;

/// <summary>
/// Contract between the popup playback window's transport bar and the
/// MainViewModel that owns the active IPlayer. The popup is a passive
/// view: it reads state and invokes commands; the VM holds truth.
/// </summary>
public interface ITransportHost
{
    /// <summary>Play / pause toggle (bound to the player).</summary>
    void PlayPause();

    /// <summary>Stop playback and close the popup window.</summary>
    void Stop();

    /// <summary>Seek to a position in seconds.</summary>
    void SeekTo(double seconds);

    TimeSpan Position { get; }
    TimeSpan Duration { get; }
    bool IsPlaying { get; }

    /// <summary>Display text for the Play/Pause button (▶ / ‖).</summary>
    string PlayPauseGlyph { get; }
}