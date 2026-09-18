using System.Reflection;
using Kiseki.Core.Entities;

namespace Kiseki.Tests;

internal static class TestCoverState
{
    private static readonly PropertyInfo CoverUrlProperty =
        typeof(MediaWork).GetProperty(nameof(MediaWork.CoverUrl))!;

    private static readonly PropertyInfo CoverSourceProperty =
        typeof(MediaWork).GetProperty(nameof(MediaWork.CoverSource))!;

    public static void SetLegacyUnknown(MediaWork work, string coverUrl)
    {
        CoverUrlProperty.SetValue(work, coverUrl);
        CoverSourceProperty.SetValue(work, MediaCoverSource.LegacyUnknown);
    }
}
