using System.Collections.Concurrent;
using Rafael.Portfolio.Modules.Assistant.Application;

namespace Rafael.Portfolio.Modules.Assistant.Infrastructure;

public sealed class InMemoryModelControlLedger : IModelControlLedger
{
    public const long DefaultDailyBudgetMicroUsd = 100_000; // $0.10 / day
    public const long DefaultMonthlyBudgetMicroUsd = 1_000_000; // $1.00 / month
    public const int DefaultMaxConcurrency = 2; // Max 2 active turns globally
    public const int DefaultMaxInputTokens = 6000;
    public const int DefaultMaxOutputTokens = 600;
    public const int DefaultMaxCalls = 2;

    private readonly long _dailyBudgetMicroUsd;
    private readonly long _monthlyBudgetMicroUsd;
    private readonly int _maxConcurrency;
    private readonly TimeProvider _timeProvider;

    private readonly Lock _lock = new();

    private string _currentDayUtc;
    private string _currentMonthUtc;
    private long _dailySpentMicroUsd;
    private long _monthlySpentMicroUsd;
    private int _activePermits;

    private readonly ConcurrentDictionary<string, ModelReservationRecord> _reservations = new();

    public InMemoryModelControlLedger(
        long? dailyBudgetMicroUsd = null,
        long? monthlyBudgetMicroUsd = null,
        int? maxConcurrency = null,
        TimeProvider? timeProvider = null)
    {
        _dailyBudgetMicroUsd = dailyBudgetMicroUsd is > 0 ? dailyBudgetMicroUsd.Value : DefaultDailyBudgetMicroUsd;
        _monthlyBudgetMicroUsd = monthlyBudgetMicroUsd is > 0 ? monthlyBudgetMicroUsd.Value : DefaultMonthlyBudgetMicroUsd;
        _maxConcurrency = maxConcurrency is > 0 ? maxConcurrency.Value : DefaultMaxConcurrency;
        _timeProvider = timeProvider ?? TimeProvider.System;

        var now = _timeProvider.GetUtcNow();
        _currentDayUtc = GetDayKey(now);
        _currentMonthUtc = GetMonthKey(now);
    }

    public int ActivePermits => _activePermits;
    public long DailySpentMicroUsd => _dailySpentMicroUsd;
    public long MonthlySpentMicroUsd => _monthlySpentMicroUsd;
    public int ReservationCount => _reservations.Count;

    public ValueTask<ModelReservationResult> TryReserveAsync(
        string reservationId,
        long maxEstimatedCostMicroUsd,
        int maxInputTokens = DefaultMaxInputTokens,
        int maxOutputTokens = DefaultMaxOutputTokens,
        int maxCalls = DefaultMaxCalls,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reservationId);
        if (maxEstimatedCostMicroUsd <= 0)
        {
            return ValueTask.FromResult(ModelReservationResult.Denied(ModelReservationDenialReason.InvalidTariffOrPolicy));
        }

        if (maxInputTokens > DefaultMaxInputTokens || maxOutputTokens > DefaultMaxOutputTokens || maxCalls > DefaultMaxCalls)
        {
            return ValueTask.FromResult(ModelReservationResult.Denied(ModelReservationDenialReason.InvalidTariffOrPolicy));
        }

        lock (_lock)
        {
            var now = _timeProvider.GetUtcNow();
            UpdatePeriodRollover(now);

            if (_reservations.ContainsKey(reservationId))
            {
                return ValueTask.FromResult(ModelReservationResult.Denied(ModelReservationDenialReason.DuplicateReservationId));
            }

            if (_activePermits >= _maxConcurrency)
            {
                return ValueTask.FromResult(ModelReservationResult.Denied(ModelReservationDenialReason.ConcurrencyLimitReached, TimeSpan.FromSeconds(1)));
            }

            if (_dailySpentMicroUsd + maxEstimatedCostMicroUsd > _dailyBudgetMicroUsd)
            {
                return ValueTask.FromResult(ModelReservationResult.Denied(ModelReservationDenialReason.DailyBudgetExhausted, TimeSpan.FromSeconds(60)));
            }

            if (_monthlySpentMicroUsd + maxEstimatedCostMicroUsd > _monthlyBudgetMicroUsd)
            {
                return ValueTask.FromResult(ModelReservationResult.Denied(ModelReservationDenialReason.MonthlyBudgetExhausted, TimeSpan.FromHours(1)));
            }

            _activePermits++;
            _dailySpentMicroUsd += maxEstimatedCostMicroUsd;
            _monthlySpentMicroUsd += maxEstimatedCostMicroUsd;

            var record = new ModelReservationRecord(
                reservationId,
                maxEstimatedCostMicroUsd,
                maxInputTokens,
                maxOutputTokens,
                maxCalls,
                now,
                HasActivePermit: true,
                IsCommitted: false);

            _reservations[reservationId] = record;

            return ValueTask.FromResult(ModelReservationResult.Granted(reservationId));
        }
    }

    public ValueTask<bool> CommitAsync(
        string reservationId,
        long actualCostMicroUsd,
        int inputTokensUsed,
        int outputTokensUsed,
        int callsMade,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reservationId);
        if (actualCostMicroUsd < 0)
        {
            actualCostMicroUsd = 0;
        }

        lock (_lock)
        {
            if (!_reservations.TryGetValue(reservationId, out var record) || record.IsCommitted)
            {
                return ValueTask.FromResult(false);
            }

            // Reconcile estimate difference if actual cost < max reserved
            if (actualCostMicroUsd < record.MaxEstimatedCostMicroUsd)
            {
                var diff = record.MaxEstimatedCostMicroUsd - actualCostMicroUsd;
                _dailySpentMicroUsd = Math.Max(0, _dailySpentMicroUsd - diff);
                _monthlySpentMicroUsd = Math.Max(0, _monthlySpentMicroUsd - diff);
            }
            else if (actualCostMicroUsd > record.MaxEstimatedCostMicroUsd)
            {
                var extra = actualCostMicroUsd - record.MaxEstimatedCostMicroUsd;
                _dailySpentMicroUsd += extra;
                _monthlySpentMicroUsd += extra;
            }

            if (record.HasActivePermit)
            {
                _activePermits = Math.Max(0, _activePermits - 1);
            }

            _reservations[reservationId] = record with
            {
                HasActivePermit = false,
                IsCommitted = true,
                ActualCostMicroUsd = actualCostMicroUsd,
                InputTokensUsed = inputTokensUsed,
                OutputTokensUsed = outputTokensUsed,
                CallsMade = callsMade
            };

            return ValueTask.FromResult(true);
        }
    }

    public ValueTask<bool> ReleasePermitAsync(
        string reservationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reservationId);

        lock (_lock)
        {
            if (!_reservations.TryGetValue(reservationId, out var record) || !record.HasActivePermit)
            {
                return ValueTask.FromResult(false);
            }

            _activePermits = Math.Max(0, _activePermits - 1);
            _reservations[reservationId] = record with { HasActivePermit = false };

            return ValueTask.FromResult(true);
        }
    }

    private void UpdatePeriodRollover(DateTimeOffset now)
    {
        var dayKey = GetDayKey(now);
        var monthKey = GetMonthKey(now);

        if (!string.Equals(_currentDayUtc, dayKey, StringComparison.Ordinal))
        {
            _currentDayUtc = dayKey;
            _dailySpentMicroUsd = 0;
        }

        if (!string.Equals(_currentMonthUtc, monthKey, StringComparison.Ordinal))
        {
            _currentMonthUtc = monthKey;
            _monthlySpentMicroUsd = 0;
        }

        // Cleanup reservations older than 40 days
        var cutoff = now.AddDays(-40);
        foreach (var (id, record) in _reservations)
        {
            if (record.CreatedAt < cutoff && !record.HasActivePermit)
            {
                _reservations.TryRemove(id, out _);
            }
        }
    }

    private static string GetDayKey(DateTimeOffset dt) => dt.UtcDateTime.ToString("yyyy-MM-dd");
    private static string GetMonthKey(DateTimeOffset dt) => dt.UtcDateTime.ToString("yyyy-MM");
}

internal sealed record ModelReservationRecord(
    string ReservationId,
    long MaxEstimatedCostMicroUsd,
    int MaxInputTokens,
    int MaxOutputTokens,
    int MaxCalls,
    DateTimeOffset CreatedAt,
    bool HasActivePermit,
    bool IsCommitted,
    long ActualCostMicroUsd = 0,
    int InputTokensUsed = 0,
    int OutputTokensUsed = 0,
    int CallsMade = 0);
