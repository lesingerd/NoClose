namespace NoClose.Core.Guards;

public enum GuardId { ExecutionState, ScreenSaver, PowerPlan, ActiveHours, EndSession, SilentMedia }

public enum GuardStatus { Off, Applied, Unavailable, NeedsElevation, BlockedByPolicy, Reverted, Error, Degraded }
