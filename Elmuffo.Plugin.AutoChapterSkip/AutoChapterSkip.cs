using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
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
        private readonly object _currentPositionsLock = new();
        private readonly Dictionary<string, long?> _currentPositions;
        private readonly ISessionManager _sessionManager;

        /// <summary>
        /// Initializes a new instance of the <see cref="AutoChapterSkip"/> class.
        /// </summary>
        /// <param name="sessionManager">Session manager.</param>
        public AutoChapterSkip(
            ISessionManager sessionManager)
        {
            _currentPositions = new Dictionary<string, long?>();
            _sessionManager = sessionManager;
        }

        /// <summary>
        /// Set it up.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>Task.</returns>
        public Task StartAsync(CancellationToken cancellationToken)
        {
            _sessionManager.PlaybackStopped += SessionManager_PlaybackStopped;
            _sessionManager.PlaybackProgress += SessionManager_PlaybackProgress;
            return Task.CompletedTask;
        }

        private void SessionManager_PlaybackProgress(object? sender, PlaybackProgressEventArgs e)
        {
            var match = Plugin.Instance!.Configuration.Match;

            if (string.IsNullOrEmpty(match))
            {
                return;
            }

            var chapters = e.Session.NowPlayingItem.Chapters;
            var regex = new Regex(match);
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

            if (chapterName is null || !regex.IsMatch(chapterName))
            {
                return;
            }

            var send = (long? ticks) =>
            {
                Lock(() => _currentPositions[e.Session.Id] = ticks);

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
            };

            ++remainingChaptersIdx;
            long? nextChapterTicks = null;
            for (var i = remainingChaptersIdx; i < chapters.Count; ++i)
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
                if (e.PlaybackPositionTicks < e.Item.RunTimeTicks)
                {
                    for (var i = remainingChaptersIdx; i < chapters.Count; ++i)
                    {
                        var input = chapters[i].Name;
                        if (input != null && !regex.IsMatch(input))
                        {
                            return;
                        }
                    }

                    send(e.Item.RunTimeTicks);
                }

                return;
            }

            long? previousChapterTicks = null;

            Lock(() => _currentPositions.TryGetValue(e.Session.Id, out previousChapterTicks));

            if (e.PlaybackPositionTicks <= previousChapterTicks)
            {
                return;
            }

            send(nextChapterTicks);
        }

        private void SessionManager_PlaybackStopped(object? sender, PlaybackStopEventArgs e)
        {
            Lock(() => _currentPositions.Remove(e.Session.Id));
        }

        private void Lock(Action work)
        {
            lock (_currentPositionsLock)
            {
                work();
            }
        }

        /// <summary>
        /// Protected dispose.
        /// </summary>
        /// <param name="cancellationToken">Dispose.</param>
        /// <returns>Task.</returns>
        public Task StopAsync(CancellationToken cancellationToken)
        {
            _sessionManager.PlaybackStopped -= SessionManager_PlaybackStopped;
            _sessionManager.PlaybackProgress -= SessionManager_PlaybackProgress;
            return Task.CompletedTask;
        }
    }
}
