namespace PointBlank.Sim.Tests;

public class SmokeTests
{
    // Trivial on purpose: proves the test project builds, runs and can use Sim.
    [Fact]
    public void Test_project_can_use_Sim()
    {
        Assert.Equal("Sim", PointBlank.Sim.SimInfo.Name);
    }
}
