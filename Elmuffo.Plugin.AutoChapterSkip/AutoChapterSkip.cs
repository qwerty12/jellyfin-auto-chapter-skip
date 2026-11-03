using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Elmuffo.Plugin.AutoChapterSkip
{
    /// <summary>
    /// Automatically skip chapters matching regex.
    /// Commands clients to seek to the end of matched chapters as soon as they start playing them.
    /// </summary>
    public sealed class AutoChapterSkip : IHostedService
    {
        // Warm-up probe (experimental): shortly before a matching chapter that starts late in the file, the client is
        // sent a silent ForceKeepAlive, so the connection has just carried traffic when the skip commands follow.
        // Nothing observes the client's reply, so whether this helps is only found out by trying it.
        private const int KeepAliveTimeoutSeconds = 60;
        private const long WarmUpMinChapterStartTicks = 10 * TimeSpan.TicksPerMinute;
        private const long WarmUpLeadTicks = 10 * TimeSpan.TicksPerSecond;

        private static readonly TimeSpan MaxSilence = TimeSpan.FromSeconds(60);

        private readonly ConcurrentDictionary<string, long> _currentPositions;
        private readonly ConcurrentDictionary<string, long> _warmedUpChapters;
        private readonly ISessionManager _sessionManager;
        private readonly ILogger<AutoChapterSkip> _logger;
        private Regex? _matchRegex;

        /// <summary>
        /// Initializes a new instance of the <see cref="AutoChapterSkip"/> class.
        /// </summary>
        /// <param name="sessionManager">Session manager.</param>
        /// <param name="logger">Logger.</param>
        public AutoChapterSkip(
            ISessionManager sessionManager,
            ILogger<AutoChapterSkip> logger)
        {
            _currentPositions = new ConcurrentDictionary<string, long>();
            _warmedUpChapters = new ConcurrentDictionary<string, long>();
            _sessionManager = sessionManager;
            _logger = logger;
        }

        /// <summary>
        /// Set it up.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Task.</returns>
        public Task StartAsync(CancellationToken cancellationToken)
        {
            Plugin_ConfigurationChanged(null, null);
            _sessionManager.PlaybackStart += SessionManager_PlaybackStartOrStop;
            _sessionManager.PlaybackStopped += SessionManager_PlaybackStartOrStop;
            _sessionManager.PlaybackProgress += SessionManager_PlaybackProgress;
            Plugin.Instance!.ConfigurationChanged += Plugin_ConfigurationChanged;
            return Task.CompletedTask;
        }

        private void Plugin_ConfigurationChanged(object? sender, BasePluginConfiguration? e)
        {
            var match = Plugin.Instance!.Configuration.Match;
            if (string.Equals(_matchRegex?.ToString() ?? string.Empty, match, StringComparison.Ordinal))
            {
                return;
            }

            Regex? regex = null;
            if (!string.IsNullOrEmpty(match))
            {
                try
                {
                    regex = new Regex(match, RegexOptions.ExplicitCapture | RegexOptions.Compiled | RegexOptions.CultureInvariant);
                }
                catch (RegexParseException ex)
                {
                    _logger.LogError(ex, "Invalid match pattern '{Pattern}', no chapters will be skipped", match);
                }
            }

            _matchRegex = regex;
        }

        private static bool CanDeliver(SessionInfo session)
            => session.IsActive && DateTime.UtcNow - session.LastPlaybackCheckIn <= MaxSilence;

        private bool TryClaimWarmUp(string sessionId, long chapterStartTicks)
        {
            while (true)
            {
                if (!_warmedUpChapters.TryGetValue(sessionId, out var warmedUpTicks))
                {
                    if (_warmedUpChapters.TryAdd(sessionId, chapterStartTicks))
                    {
                        return true;
                    }
                }
                else if (warmedUpTicks == chapterStartTicks)
                {
                    return false;
                }
                else if (_warmedUpChapters.TryUpdate(sessionId, chapterStartTicks, warmedUpTicks))
                {
                    return true;
                }
            }
        }

        private void SessionManager_PlaybackProgress(object? sender, PlaybackProgressEventArgs e)
        {
            var chapters = e.Session.NowPlayingItem?.Chapters;
            if (chapters is null || chapters.Count == 0)
            {
                return;
            }

            var regex = _matchRegex;
            if (regex is null || e.PlaybackPositionTicks is null)
            {
                return;
            }

            var playbackPositionTicks = e.PlaybackPositionTicks.GetValueOrDefault();
            var chapterCount = chapters.Count;
            // Chapters are always in ascending StartPositionTicks order, so instead of
            // scanning backwards from the end on every single tick (O(n), worst case
            // when playback is still near the start of the item), binary search for the
            // last chapter that has already started. Same result as a "scan from the
            // end, break on first hit" loop, just O(log n).
            var lo = 0;
            var hi = chapterCount;
            while (lo < hi)
            {
                var mid = lo + ((hi - lo) >> 1);
                if (chapters[mid].StartPositionTicks < playbackPositionTicks)
                {
                    lo = mid + 1;
                }
                else
                {
                    hi = mid;
                }
            }

            // lo is now the first chapter that has not started yet. If it is a matching chapter that starts late in
            // the file and is about to begin, warm the connection up once, ahead of the skip commands.
            if (lo < chapterCount)
            {
                var nextChapter = chapters[lo];
                var nextStartTicks = nextChapter.StartPositionTicks;
                var untilNextTicks = nextStartTicks - playbackPositionTicks;
                if (nextStartTicks >= WarmUpMinChapterStartTicks
                    && untilNextTicks is > 0 and <= WarmUpLeadTicks
                    && nextChapter.Name is { } nextName
                    && regex.IsMatch(nextName)
                    && e.Session.IsActive
                    && TryClaimWarmUp(e.Session.Id, nextStartTicks))
                {
                    var deviceId = e.Session.DeviceId;
                    if (!string.IsNullOrEmpty(deviceId))
                    {
                        _sessionManager.SendMessageToUserDeviceSessions(
                            deviceId,
                            SessionMessageType.ForceKeepAlive,
                            KeepAliveTimeoutSeconds,
                            CancellationToken.None);
                    }
                }
            }

            var remainingChaptersIdx = lo - 1;
            if (remainingChaptersIdx < 0)
            {
                return;
            }

            var chapterName = chapters[remainingChaptersIdx].Name;
            if (chapterName is null || !regex.IsMatch(chapterName))
            {
                return;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            void Send(long? ticks, string chapterName, SessionInfo session)
            {
                if (!CanDeliver(session))
                {
                    return;
                }

                var sessionId = session.Id;
                _sessionManager.SendMessageCommand(
                    sessionId,
                    sessionId,
                    new MessageCommand
                    {
                        Header = "Auto Chapter Skip",
                        Text = chapterName + " skipped",
                        TimeoutMs = 2000,
                    },
                    CancellationToken.None);

                // ControllingUserId deliberately left unset: SendPlaystateCommand always
                // overwrites it from the resolved controlling session whenever the
                // controllingSessionId argument (sessionId, below) is non-empty - which
                // it always is here - so anything set on this object is discarded anyway.
                _sessionManager.SendPlaystateCommand(
                    sessionId,
                    sessionId,
                    new PlaystateRequest
                    {
                        Command = PlaystateCommand.Seek,
                        SeekPositionTicks = ticks
                    },
                    CancellationToken.None);
            }

            // Once a skip has been sent for a target, nothing more is sent until playback passes that target. A lost
            // seek is not retried, and the user can seek back into a skipped chapter and watch it.
            var sessionId = e.Session.Id;
            if (_currentPositions.TryGetValue(sessionId, out var previousChapterTicks) && playbackPositionTicks <= previousChapterTicks)
            {
                return;
            }

            ++remainingChaptersIdx;
            long? nextChapterTicks = null;
            for (var i = remainingChaptersIdx; i < chapterCount; ++i)
            {
                var input = chapters[i].Name;
                if (input is not null && !regex.IsMatch(input))
                {
                    nextChapterTicks = chapters[i].StartPositionTicks;
                    break;
                }
            }

            if (nextChapterTicks is null)
            {
                // NOTE: the loop above already walked every remaining chapter
                // (remainingChaptersIdx .. chapterCount-1) and found none that fails to
                // match; that's the only way nextChapterTicks can still be null here.
                // Re-checking the same chapters against the same regex again would
                // always come back "still all matching" - it's a guaranteed no-op, so
                // there is nothing left to verify before treating this as the last,
                // to-be-skipped chapter running to the end of the item. The
                // playbackPositionTicks < runTimeTicks check below is also already the
                // "don't seek past where we already are" guard, since the seek target
                // here *is* runTimeTicks.
                var runTimeTicks = e.Item?.RunTimeTicks;
                if (runTimeTicks is { } targetTicks2 && playbackPositionTicks < targetTicks2)
                {
                    _currentPositions[sessionId] = targetTicks2;
                    Send(runTimeTicks, chapterName, e.Session);
                }

                return;
            }

            var targetTicks = nextChapterTicks.GetValueOrDefault();
            _currentPositions[sessionId] = targetTicks;
            if (targetTicks <= playbackPositionTicks)
            {
                return;
            }

            Send(nextChapterTicks, chapterName, e.Session);
        }

        // Cleared on start as well as on stop: a session can play several items in a row, and a client that never reports
        // a stop would otherwise leave the previous item's targets behind to suppress skipping in the next one.
        private void SessionManager_PlaybackStartOrStop(object? sender, PlaybackProgressEventArgs e)
        {
            _currentPositions.TryRemove(e.Session.Id, out _);
            _warmedUpChapters.TryRemove(e.Session.Id, out _);
        }

        /// <summary>
        /// Protected dispose.
        /// </summary>
        /// <param name="cancellationToken">Dispose.</param>
        /// <returns>Task.</returns>
        public Task StopAsync(CancellationToken cancellationToken)
        {
            _sessionManager.PlaybackProgress -= SessionManager_PlaybackProgress;
            _sessionManager.PlaybackStopped -= SessionManager_PlaybackStartOrStop;
            _sessionManager.PlaybackStart -= SessionManager_PlaybackStartOrStop;
            Plugin.Instance!.ConfigurationChanged -= Plugin_ConfigurationChanged;
            _matchRegex = null;
            _currentPositions.Clear();
            _warmedUpChapters.Clear();
            return Task.CompletedTask;
        }
    }
}
