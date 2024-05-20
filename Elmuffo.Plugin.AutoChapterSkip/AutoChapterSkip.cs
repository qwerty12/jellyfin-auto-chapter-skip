using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Hosting;

namespace Elmuffo.Plugin.AutoChapterSkip
{
    /// <summary>
    /// Automatically skip chapters matching regex.
    /// Commands clients to seek to the end of matched chapters as soon as they start playing them.
    /// </summary>
    public class AutoChapterSkip : IHostedService
    {
        private readonly ConcurrentDictionary<string, long?> _currentPositions;
        private readonly ISessionManager _sessionManager;
        private Regex? _matchRegex;

        /// <summary>
        /// Initializes a new instance of the <see cref="AutoChapterSkip"/> class.
        /// </summary>
        /// <param name="sessionManager">Session manager.</param>
        public AutoChapterSkip(
            ISessionManager sessionManager)
        {
            _currentPositions = new ConcurrentDictionary<string, long?>();
            _sessionManager = sessionManager;
        }

        /// <summary>
        /// Set it up.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Task.</returns>
        public Task StartAsync(CancellationToken cancellationToken)
        {
            ApplySettingsFromConfig();
            _sessionManager.PlaybackStopped += SessionManager_PlaybackStopped;
            _sessionManager.PlaybackProgress += SessionManager_PlaybackProgress;
            Plugin.Instance!.ConfigurationChanged += Plugin_ConfigurationChanged;
            return Task.CompletedTask;
        }

        private void Plugin_ConfigurationChanged(object? sender, BasePluginConfiguration e)
        {
            ApplySettingsFromConfig();
        }

        private void ApplySettingsFromConfig()
        {
            var match = Plugin.Instance!.Configuration.Match;
            _matchRegex = !string.IsNullOrEmpty(match) ? new Regex(match, RegexOptions.Compiled | RegexOptions.NonBacktracking) : null;
        }

        private void SessionManager_PlaybackProgress(object? sender, PlaybackProgressEventArgs e)
        {
            if (_matchRegex is null)
            {
                return;
            }

            var chapters = e.Session.NowPlayingItem.Chapters;
            if (chapters is null || chapters.Count == 0)
            {
                return;
            }

            var remainingChaptersIdx = -1;
            string? chapterName = null;

            for (var i = chapters.Count - 1; i >= 0; --i)
            {
                if (chapters[i].StartPositionTicks < e.PlaybackPositionTicks)
                {
                    remainingChaptersIdx = i;
                    chapterName = chapters[i].Name;
                    break;
                }
            }

            if (chapterName is null || !_matchRegex.IsMatch(chapterName))
            {
                return;
            }

            void Send(long? ticks)
            {
                _currentPositions[e.Session.Id] = ticks;

                _sessionManager.SendPlaystateCommand(
                   e.Session.Id,
                   e.Session.Id,
                   new PlaystateRequest
                   {
                       Command = PlaystateCommand.Seek,
                       ControllingUserId = e.Session.UserId.ToString("N"),
                       SeekPositionTicks = ticks
                   },
                   CancellationToken.None);

                _sessionManager.SendMessageCommand(
                    e.Session.Id,
                    e.Session.Id,
                    new MessageCommand
                    {
                        Header = "Auto Chapter Skip",
                        Text = chapterName + " skipped",
                        TimeoutMs = 2000,
                    },
                    CancellationToken.None);
            }

            ++remainingChaptersIdx;
            long? nextChapterTicks = null;
            for (var i = remainingChaptersIdx; i < chapters.Count; ++i)
            {
                var input = chapters[i].Name;
                if (input is not null && !_matchRegex.IsMatch(input))
                {
                    nextChapterTicks = chapters[i].StartPositionTicks;
                    break;
                }
            }

            if (nextChapterTicks is null)
            {
                if (e.PlaybackPositionTicks < e.Item.RunTimeTicks)
                {
                    for (var i = remainingChaptersIdx; i < chapters.Count; ++i)
                    {
                        var input = chapters[i].Name;
                        if (input is not null && !_matchRegex.IsMatch(input))
                        {
                            return;
                        }
                    }

                    Send(e.Item.RunTimeTicks);
                }

                return;
            }

            _currentPositions.TryGetValue(e.Session.Id, out var previousChapterTicks);

            if (e.PlaybackPositionTicks <= previousChapterTicks)
            {
                return;
            }

            Send(nextChapterTicks);
        }

        private void SessionManager_PlaybackStopped(object? sender, PlaybackStopEventArgs e)
        {
            _currentPositions.TryRemove(e.Session.Id, out _);
        }

        /// <summary>
        /// Protected dispose.
        /// </summary>
        /// <param name="cancellationToken">Dispose.</param>
        /// <returns>Task.</returns>
        public Task StopAsync(CancellationToken cancellationToken)
        {
            _sessionManager.PlaybackProgress -= SessionManager_PlaybackProgress;
            _sessionManager.PlaybackStopped -= SessionManager_PlaybackStopped;
            Plugin.Instance!.ConfigurationChanged -= Plugin_ConfigurationChanged;
            _matchRegex = null;
            _currentPositions.Clear();
            return Task.CompletedTask;
        }
    }
}
