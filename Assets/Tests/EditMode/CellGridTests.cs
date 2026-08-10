using NUnit.Framework;
using Biomes;

/// <summary>
/// Guards the property the absolute-cell-resolution change exists to buy: the CA's grid —
/// and therefore its on-screen cell size — must not move when the master resolution moves.
/// The old `cellResolutionScale` was a fraction of master, so 1080p and 4K produced different
/// grids from identical settings. Resolution_DoesNotChangeGrid is the test that would have failed.
/// </summary>
public class CellGridTests
{
    [Test]
    public void Height_ClampsToMinimum()
    {
        Assert.That(CellGrid.Height(2), Is.EqualTo(CellGrid.MinRez));
    }

    [Test]
    public void Height_PassesThroughAboveMinimum()
    {
        Assert.That(CellGrid.Height(540), Is.EqualTo(540));
    }

    [Test]
    public void Width_PreservesSixteenNineAspect()
    {
        // 540 * 3840/2160 = 960
        Assert.That(CellGrid.Width(540, 3840, 2160), Is.EqualTo(960));
    }

    [Test]
    public void Width_PreservesUltraWideAspect()
    {
        // 540 * 9472/900 = 5683.2 -> 5683. Deliberately asserted: an absolute height on an
        // 11.84:1 canvas is EXPENSIVE (3.07 M cells). Authors must lower cellRezHeight there.
        Assert.That(CellGrid.Width(540, 9472, 900), Is.EqualTo(5683));
    }

    [Test]
    public void Resolution_DoesNotChangeGrid()
    {
        Assert.That(CellGrid.Width(540, 1920, 1080), Is.EqualTo(CellGrid.Width(540, 3840, 2160)));
        Assert.That(CellGrid.CellCount(540, 1920, 1080), Is.EqualTo(CellGrid.CellCount(540, 3840, 2160)));
    }

    [Test]
    public void Width_HandlesDegenerateMaster()
    {
        Assert.That(CellGrid.Width(540, 3840, 0), Is.EqualTo(540));
        Assert.That(CellGrid.Width(540, 0, 2160), Is.EqualTo(540));
    }

    [Test]
    public void Width_ClampsToMinimum()
    {
        Assert.That(CellGrid.Width(8, 1, 4096), Is.EqualTo(CellGrid.MinRez));
    }

    [Test]
    public void CellCount_IsWidthTimesHeight()
    {
        Assert.That(CellGrid.CellCount(540, 3840, 2160), Is.EqualTo(960 * 540));
    }
}
