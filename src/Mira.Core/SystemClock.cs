namespace Mira.Core;

using Mira.Core.Interfaces;

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
