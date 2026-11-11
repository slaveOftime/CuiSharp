using System.Threading.Tasks;
using CuiSharp;

namespace CuiSharp.Tests;

/// <summary>
/// Enum values are part of the ABI: they are passed as plain ints to C, which
/// casts them to its own enums. These tests pin the numbers so a reordering in
/// either cui.h or Types.cs cannot silently change the meaning of a UI.
/// </summary>
public class EnumContractTests
{
    [Fact]
    public void TokensMatchCuiToken()
    {
        // cui_token: NONE = 0, then the semantic colors in declaration order.
        Assert.Equal(0, (int)Token.None);
        Assert.Equal(1, (int)Token.Background);
        Assert.Equal(2, (int)Token.Surface);
        Assert.Equal(3, (int)Token.SurfaceRaised);
        Assert.Equal(4, (int)Token.SurfaceMuted);
        Assert.Equal(5, (int)Token.Foreground);
        Assert.Equal(6, (int)Token.TextSecondary);
        Assert.Equal(7, (int)Token.TextMuted);
        Assert.Equal(8, (int)Token.Border);
        Assert.Equal(10, (int)Token.BorderStrong);
        Assert.Equal(11, (int)Token.Accent);
        Assert.Equal(15, (int)Token.Success);
        Assert.Equal(17, (int)Token.Danger);
        Assert.Equal(29, (int)Token.Series1);
        Assert.Equal(34, (int)Token.Series6);
    }

    [Fact]
    public void ButtonVariantsMatchCuiButtonVariant()
    {
        Assert.Equal(0, (int)ButtonVariant.Default);
        Assert.Equal(1, (int)ButtonVariant.Secondary);
        Assert.Equal(2, (int)ButtonVariant.Primary);
        Assert.Equal(6, (int)ButtonVariant.Link);
    }

    [Fact]
    public void SizesAndLayoutEnumsMatch()
    {
        // cui_size_kind
        Assert.Equal(SizeKind.Grow, Size.Fill.Kind);
        Assert.Equal(SizeKind.Fixed, Size.Px(4).Kind);
        Assert.Equal(SizeKind.Percent, Size.Percent(50).Kind);
        Assert.Equal(SizeKind.Fit, Size.Fit.Kind);

        // cui_dir, cui_align, cui_justify
        Assert.Equal(0, (int)Dir.Column);
        Assert.Equal(1, (int)Dir.Row);
        Assert.Equal(0, (int)Align.Default);
        Assert.Equal(4, (int)Align.Stretch);
        Assert.Equal(3, (int)Justify.Between);
    }

    [Fact]
    public void TonesMatchCuiTone()
    {
        Assert.Equal(0, (int)Tone.Neutral);
        Assert.Equal(1, (int)Tone.Info);
        Assert.Equal(2, (int)Tone.Success);
        Assert.Equal(3, (int)Tone.Warning);
        Assert.Equal(4, (int)Tone.Danger);
        Assert.Equal(8, (int)Tone.Accent);
    }

    [Fact]
    public void AccentsMatchCuiAccent()
    {
        Assert.Equal(0, (int)Accent.Neutral);
        Assert.Equal(1, (int)Accent.Violet);
        Assert.Equal(7, (int)Accent.Rose);
    }

    [Fact]
    public void KeysFollowCuiKeyOrder()
    {
        // The block order in cui.h: specials, arrows, Home..PageDown, A-Z, 0-9, punctuation, F1-F12.
        Assert.Equal(0, (int)Key.None);
        Assert.Equal(1, (int)Key.Tab);
        Assert.Equal(14, (int)Key.PageDown);
        Assert.Equal(15, (int)Key.A);
        Assert.Equal(24, (int)Key.J);
        Assert.Equal(25, (int)Key.K);
        Assert.Equal(40, (int)Key.Z);
        Assert.Equal(41, (int)Key.D0);
        Assert.Equal(50, (int)Key.D9);
        Assert.Equal(56, (int)Key.F1);
        Assert.Equal(67, (int)Key.F12);
    }

    [Fact]
    public void ModsAreBitFlagsWithCtrlAsPrimaryOnWindows()
    {
        Assert.Equal(1, (int)Mods.Shift);
        Assert.Equal(2, (int)Mods.Ctrl);
        Assert.Equal(4, (int)Mods.Alt);
        Assert.Equal(8, (int)Mods.Super);
        Assert.Equal(Mods.Ctrl, Mods.Primary);
    }

    [Fact]
    public void CuiTaskStatusCoexistsWithBclTaskStatus()
    {
        // CuiSharp.TaskStatus used to collide with System.Threading.Tasks.TaskStatus:
        // with both usings in scope, an unqualified TaskStatus was ambiguous. The
        // binding is renamed CuiTaskStatus, so both names resolve unqualified here.
        Assert.NotEqual(typeof(CuiTaskStatus), typeof(TaskStatus));

        // cui_task_status: the ABI numbers the C side casts to.
        Assert.Equal(0, (int)CuiTaskStatus.Pending);
        Assert.Equal(1, (int)CuiTaskStatus.Running);
        Assert.Equal(2, (int)CuiTaskStatus.Done);
        Assert.Equal(3, (int)CuiTaskStatus.Failed);
        Assert.Equal(4, (int)CuiTaskStatus.Skipped);

        // The BCL enum keeps its own, different numbering (Canceled = 6, not in CUI).
        Assert.Equal(0, (int)TaskStatus.Created);
        Assert.Equal(6, (int)TaskStatus.Canceled);
    }

    [Fact]
    public void PlacementAndSidesMatch()
    {
        Assert.Equal(0, (int)Placement.BottomStart);
        Assert.Equal(9, (int)Placement.Left);
        Assert.Equal(0, (int)Side.Right);
        Assert.Equal(1, (int)Side.Left);
    }
}
