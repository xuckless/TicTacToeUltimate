using TicTacToeUltimate.Client.Services;
using TicTacToeUltimate.Client.State;
using TicTacToeUltimate.Contracts;

namespace TicTacToeUltimate.Client.Stores;

/// <summary>
/// Bans arrive as 403 <c>banned</c> on any request (the app then shows only the ban notice); warnings and mutes arrive live
/// over the hub and are shown once. Reports and appeals go through here.
/// </summary>
public sealed class ModerationStore : StoreBase<ModerationState>, IModerationStore
{
    private readonly TttApiClient _api;

    /// <summary>Creates the store.</summary>
    public ModerationStore(TttApiClient api, IRealtimeTransport rt) : base(ModerationState.Empty)
    {
        _api = api;
        api.Banned += p => Update(s => s with { Ban = p });
        rt.PenaltyReceived += p => Update(s => p.Kind == "ban" ? s with { Ban = p } : s with { Notice = p });
    }

    /// <inheritdoc />
    public async Task<string?> ReportAsync(ReportRequestDto report)
    {
        try { await _api.ReportAsync(report); return null; }
        catch (TttApiException e) { return e.Code ?? ErrorCodes.Internal; }
        catch (HttpRequestException) { return ErrorCodes.Timeout; }
    }

    /// <inheritdoc />
    public async Task<string?> AppealAsync(long penaltyId, string text)
    {
        try
        {
            await _api.AppealAsync(new AppealRequestDto(penaltyId, text));
            Update(s => s with
            {
                Ban = s.Ban is { } b && b.Id == penaltyId ? b with { CanAppeal = false, AppealStatus = "open" } : s.Ban,
                Notice = s.Notice is { } n && n.Id == penaltyId ? n with { CanAppeal = false, AppealStatus = "open" } : s.Notice,
            });
            await LoadStandingAsync();
            return null;
        }
        catch (TttApiException e) { return e.Code ?? ErrorCodes.Internal; }
        catch (HttpRequestException) { return ErrorCodes.Timeout; }
    }

    /// <inheritdoc />
    public async Task LoadStandingAsync()
    {
        try { var s = await _api.GetStandingAsync(); Update(x => x with { Standing = s }); }
        catch (Exception e) when (e is TttApiException or HttpRequestException) { }
    }

    /// <inheritdoc />
    public void DismissNotice() => Update(s => s with { Notice = null });
}
