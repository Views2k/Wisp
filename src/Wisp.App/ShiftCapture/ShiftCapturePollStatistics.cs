namespace Wisp.App;

internal sealed class ShiftCapturePollStatistics
{
    private readonly object _gate = new();
    private readonly long _frequency;
    private Aggregate _total, _interval;
    private long _lastStarted, _lastCompleted, _reportedAt;
    private bool _hasPrevious, _previousForeground;

    internal ShiftCapturePollStatistics(long frequency)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frequency);
        _frequency = frequency;
    }

    internal ShiftCapturePollWindow? Observe(long started, long completed,
        bool foreground, bool apiAvailable, bool singleControllerAvailable)
    {
        lock (_gate)
        {
            var validRead = started > 0 && completed >= started;
            var ordered = validRead && (!_hasPrevious || started > _lastStarted && started >= _lastCompleted);
            long? spacing = ordered && _hasPrevious ? started - _lastStarted : null;
            _total.Add(started, completed, foreground, apiAvailable, singleControllerAvailable,
                validRead, ordered, spacing, _previousForeground, _frequency);
            _interval.Add(started, completed, foreground, apiAvailable, singleControllerAvailable,
                validRead, ordered, spacing, _previousForeground, _frequency);
            _hasPrevious = validRead;
            _lastStarted = started;
            _lastCompleted = completed;
            _previousForeground = foreground;
            if (!validRead) return null;
            if (_reportedAt == 0) _reportedAt = completed;
            if (completed < _reportedAt || completed - _reportedAt < _frequency) return null;
            var report = _interval.Snapshot(_frequency);
            _interval = default;
            _reportedAt = completed;
            return report;
        }
    }

    internal ShiftCapturePollingSnapshot Snapshot()
    {
        lock (_gate) return new(_total.Snapshot(_frequency), _interval.Snapshot(_frequency));
    }

    private struct Aggregate
    {
        private long _polls, _foreground, _apiAvailable, _available, _foregroundAvailable, _nonMonotonic;
        private long _started, _completed, _gaps20, _gaps50, _foregroundGaps20, _foregroundGaps50;
        private Range _read, _spacing, _foregroundSpacing;

        internal void Add(long started, long completed, bool foreground, bool apiAvailable, bool available,
            bool validRead, bool ordered, long? spacing, bool previousForeground, long frequency)
        {
            _polls++;
            if (foreground) _foreground++;
            if (apiAvailable) _apiAvailable++;
            if (available) _available++;
            if (foreground && available) _foregroundAvailable++;
            if (!ordered) _nonMonotonic++;
            if (validRead)
            {
                if (_started == 0) _started = started;
                _completed = Math.Max(_completed, completed);
                _read.Add(completed - started);
            }
            if (spacing is not { } ticks) return;
            _spacing.Add(ticks);
            if ((double)ticks / frequency > .02) _gaps20++;
            if ((double)ticks / frequency > .05) _gaps50++;
            if (!foreground || !previousForeground) return;
            _foregroundSpacing.Add(ticks);
            if ((double)ticks / frequency > .02) _foregroundGaps20++;
            if ((double)ticks / frequency > .05) _foregroundGaps50++;
        }

        internal readonly ShiftCapturePollWindow Snapshot(long frequency) => new(frequency,
            _started, _completed, _polls, _foreground, _apiAvailable, _available, _foregroundAvailable,
            _nonMonotonic, _read.Snapshot(frequency), _spacing.Snapshot(frequency), _gaps20, _gaps50,
            _foregroundSpacing.Snapshot(frequency), _foregroundGaps20, _foregroundGaps50);
    }

    private struct Range
    {
        private long _count, _minimum, _maximum;
        private double _sum;

        internal void Add(long ticks)
        {
            if (_count++ == 0) _minimum = ticks;
            _minimum = Math.Min(_minimum, ticks);
            _maximum = Math.Max(_maximum, ticks);
            _sum += ticks;
        }

        internal readonly ShiftCapturePollTiming Snapshot(long frequency) => new(_count,
            _count == 0 ? null : (double)_minimum / frequency * 1000,
            _count == 0 ? null : (double)_maximum / frequency * 1000,
            _count == 0 ? null : _sum / _count / frequency * 1000);
    }
}

internal sealed record ShiftCapturePollingSnapshot(ShiftCapturePollWindow Total, ShiftCapturePollWindow PendingInterval);
internal sealed record ShiftCapturePollTiming(long Samples, double? MinimumMilliseconds, double? MaximumMilliseconds, double? MeanMilliseconds);
internal sealed record ShiftCapturePollWindow(long QpcFrequency, long StartedQpc, long CompletedQpc,
    long Polls, long ForegroundPolls, long ApiAvailablePolls, long SingleControllerAvailablePolls,
    long ForegroundAvailablePolls, long NonMonotonicTimestampPolls, ShiftCapturePollTiming ApiRead,
    ShiftCapturePollTiming PollSpacing, long GapsOver20Milliseconds, long GapsOver50Milliseconds,
    ShiftCapturePollTiming ForegroundPollSpacing, long ForegroundGapsOver20Milliseconds, long ForegroundGapsOver50Milliseconds)
{
    public string TimingMeaning => "API read is the measured controller query duration. Poll spacing is start-to-start, not the requested delay or physical button time. Foreground spacing requires both consecutive polls to have Forza focus; background polling intentionally uses a slower cadence.";
}
