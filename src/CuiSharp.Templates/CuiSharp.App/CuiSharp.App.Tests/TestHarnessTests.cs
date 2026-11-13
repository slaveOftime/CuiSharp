using CuiSharp;
using CuiSharp.Hosting;

namespace CuiSharp.App.Tests;

public class TestHarnessTests
{
    [Fact]
    public void ASteadyStateAllocationFailsTheBudgetGate()
    {
        using var page = new AllocatingPage();

        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => TestHarness.AssertZeroAllocations(page));
    }

    [Theory]
    [InlineData(2)] // warm-up
    [InlineData(6)] // first measured batch
    [InlineData(20)] // later measured batch
    public void ARenderFaultCannotMasqueradeAsAZeroAllocationFrame(int faultAt)
    {
        using var page = new FaultingPage(faultAt);

        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => TestHarness.AssertZeroAllocations(page));
    }

    [Fact]
    public void PreparationCannotClearAStartupFaultBeforeTheGateChecksIt()
    {
        using var page = new FaultingPage(faultAt: 1);
        var prepared = false;

        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => TestHarness.AssertZeroAllocations(page, prepare: app =>
        {
            prepared = true;
            app.Frame();
        }));

        Assert.False(prepared);
    }

    private sealed class AllocatingPage : Component
    {
        protected override void Draw(Ui ui)
        {
            // Intentional allocation: the helper must reject a page that spends managed memory.
            GC.KeepAlive(new byte[128]);
            ui.Text("Allocated");
        }
    }

    private sealed class FaultingPage(int faultAt) : Component
    {
        private int _frames;

        protected override void Draw(Ui ui)
        {
            if (++_frames == faultAt)
            {
                throw new InvalidOperationException("Intentional render fault");
            }

            ui.Text("Healthy");
        }
    }
}
