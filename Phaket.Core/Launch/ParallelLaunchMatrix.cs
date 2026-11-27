using Phaket.Core.Omrl;

namespace Phaket.Core.Launch;

public enum ParallelLaunchSupportStatus { Supported, NotSupported, Unknown }

// Stable reason code plus both user-facing languages. Unknown is not a promise of support.
public sealed record ParallelLaunchReason(string Code, string MessagePl, string MessageEn)
{
    public string Message(string locale) =>
        string.Equals(locale, "pl-PL", StringComparison.OrdinalIgnoreCase) ? MessagePl : MessageEn;

    public static ParallelLaunchReason SharedSaves { get; } = Create("shared-saves", ErrorCodes.ParallelLaunchSharedSaves);
    public static ParallelLaunchReason SteamSingleInstance { get; } = Create("steam-single-instance", ErrorCodes.ParallelLaunchSteamSingleInstance);
    public static ParallelLaunchReason EpicSingleInstance { get; } = Create("epic-single-instance", ErrorCodes.ParallelLaunchEpicSingleInstance);
    public static ParallelLaunchReason PortConflict { get; } = Create("port-conflict", ErrorCodes.ParallelLaunchPortConflict);
    public static ParallelLaunchReason Unverified { get; } = Create("unverified", ErrorCodes.ParallelLaunchUnverified);

    public static ParallelLaunchReason FromCode(string code) => code switch
    {
        "shared-saves" => SharedSaves,
        "steam-single-instance" => SteamSingleInstance,
        "epic-single-instance" => EpicSingleInstance,
        "port-conflict" => PortConflict,
        "unverified" => Unverified,
        _ => throw new PhaketException(ErrorCodes.ValidationFailed, ("detail", $"unknown parallel launch reason: {code}")),
    };

    public static bool IsNotSupportedCode(string code) =>
        code is "shared-saves" or "steam-single-instance" or "epic-single-instance" or "port-conflict";

    private static ParallelLaunchReason Create(string code, string errorCode) => new(
        code, ErrorMessages.Format(errorCode, "pl-PL"), ErrorMessages.Format(errorCode, "en"));
}

public sealed record ParallelLaunchDecision(ParallelLaunchSupportStatus Status, ParallelLaunchReason? Reason = null)
{
    public static ParallelLaunchDecision Supported { get; } = new(ParallelLaunchSupportStatus.Supported);
    public static ParallelLaunchDecision NotSupported(ParallelLaunchReason reason) =>
        new(ParallelLaunchSupportStatus.NotSupported, reason);
    public static ParallelLaunchDecision Unknown { get; } = new(ParallelLaunchSupportStatus.Unknown, ParallelLaunchReason.Unverified);
}

// Additive definition override. Omission means the matrix must derive a conservative answer.
public sealed record ParallelLaunchDeclaration(string Status, string? Reason = null);

public sealed record ParallelLaunch(IReadOnlyDictionary<string, ParallelLaunchDeclaration>? Os = null)
{
    public ParallelLaunchDeclaration? For(string os) => Os?.GetValueOrDefault(os);
}

// The launch identity is data: game × store × OS. The route is separate because steam://
// always means one Steam client, even when the package itself could isolate its saves.
public sealed record ParallelLaunchContext(GameDefinition Definition, string Store, string Os);

public static class ParallelLaunchMatrix
{
    public static ParallelLaunchDecision Evaluate(ParallelLaunchContext context, bool viaSteamProtocol = false)
    {
        if (viaSteamProtocol) return ParallelLaunchDecision.NotSupported(ParallelLaunchReason.SteamSingleInstance);

        var definition = context.Definition;
        if (context.Os is not ("windows" or "linux")
            || string.IsNullOrWhiteSpace(context.Store)
            || definition.StoreIds?.ContainsKey(context.Store) != true)
            return ParallelLaunchDecision.Unknown;

        // Shared or undetermined saves are a physical conflict, not something a future
        // "supported" declaration may safely override.
        if (definition.SavesIsolationFor(context.Os).Mode != SavesIsolationMode.EnvRedirect)
            return ParallelLaunchDecision.NotSupported(ParallelLaunchReason.SharedSaves);

        if (definition.ParallelLaunchFor(context.Os) is not { } declaration) return ParallelLaunchDecision.Unknown;
        return declaration.Status == "supported"
            ? ParallelLaunchDecision.Supported
            : ParallelLaunchDecision.NotSupported(ParallelLaunchReason.FromCode(declaration.Reason ?? "unverified"));
    }
}
