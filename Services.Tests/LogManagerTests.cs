using Services.Core.Services;
using Xunit;

namespace Services.Tests
{
    public class LogManagerTests
    {
        private static readonly DateTime Base = new(2026, 10, 15, 0, 0, 0);

        private const long MB = 1024 * 1024;

        private static LogFileEntry File(string name, long length, int daysAgo) =>
            new(name, length, Base.AddDays(-daysAgo));

        private static string[] Names(IEnumerable<LogFileEntry> files) => files.Select(f => f.Path).ToArray();

        [Fact]
        public void SelectFilesToDelete_UnderLimit_SelectsNothing()
        {
            var files = new[] { File("a", 10, 3), File("b", 20, 2) };

            Assert.Empty(LogManager.SelectFilesToDelete(files, 30));
            Assert.Empty(LogManager.SelectFilesToDelete(files, 100));
        }

        [Fact]
        public void SelectFilesToDelete_OverLimit_DeletesOldestFirstUntilWithinLimit()
        {
            var files = new[] { File("new", 40, 1), File("old", 40, 3), File("mid", 40, 2) };

            // 总计 120，上限 70：删除最旧的 old(40) 后剩 80 仍超限，再删 mid 后剩 40
            var selected = LogManager.SelectFilesToDelete(files, 70);

            Assert.Equal(new[] { "old", "mid" }, Names(selected));
        }

        [Fact]
        public void SelectFilesToDelete_StopsAsSoonAsWithinLimit()
        {
            var files = new[] { File("old", 30, 3), File("mid", 30, 2), File("new", 30, 1) };

            var selected = LogManager.SelectFilesToDelete(files, 60);

            Assert.Equal(new[] { "old" }, Names(selected));
        }

        [Fact]
        public void SelectFilesToDelete_TotalExactlyAtLimit_SelectsNothing()
        {
            var files = new[] { File("a", 50, 2), File("b", 50, 1) };

            Assert.Empty(LogManager.SelectFilesToDelete(files, 100));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        public void SelectFilesToDelete_NonPositiveLimit_MeansUnlimited(long limit)
        {
            var files = new[] { File("a", 10 * MB, 2), File("b", 10 * MB, 1) };

            Assert.Empty(LogManager.SelectFilesToDelete(files, limit));
        }

        [Fact]
        public void SelectFilesToDelete_SingleFileLargerThanLimit_IsSelected()
        {
            var files = new[] { File("huge", 500, 1) };

            Assert.Equal(new[] { "huge" }, Names(LogManager.SelectFilesToDelete(files, 100)));
        }

        [Fact]
        public void SelectFilesToDelete_OrdersByLastWriteTimeNotInputOrder()
        {
            var files = new[]
            {
                new LogFileEntry("c", 10, Base.AddHours(3)),
                new LogFileEntry("a", 10, Base.AddHours(1)),
                new LogFileEntry("b", 10, Base.AddHours(2)),
            };

            var selected = LogManager.SelectFilesToDelete(files, 10);

            Assert.Equal(new[] { "a", "b" }, Names(selected));
        }

        [Fact]
        public void SelectFilesToDelete_SameTime_FallsBackToPathForStableOrder()
        {
            var files = new[]
            {
                new LogFileEntry("z", 10, Base),
                new LogFileEntry("a", 10, Base),
            };

            Assert.Equal(new[] { "a" }, Names(LogManager.SelectFilesToDelete(files, 10)));
        }

        [Fact]
        public void SelectFilesToDelete_Undeletable_StaysInTotalButIsSkipped()
        {
            var files = new[] { File("locked", 50, 3), File("mid", 30, 2), File("new", 30, 1) };
            var undeletable = new HashSet<string> { "locked" };

            // 总计 110，上限 70：最旧的 locked 无法删除，继续删 mid 后剩 80 仍超限，再删 new 后剩 50
            var selected = LogManager.SelectFilesToDelete(files, 70, undeletable);

            Assert.Equal(new[] { "mid", "new" }, Names(selected));
        }

        [Fact]
        public void SelectFilesToDelete_AllUndeletable_SelectsNothing()
        {
            var files = new[] { File("a", 100, 2), File("b", 100, 1) };

            Assert.Empty(LogManager.SelectFilesToDelete(files, 10, new HashSet<string> { "a", "b" }));
        }

        [Fact]
        public void SelectFilesToDelete_EmptyList_SelectsNothing()
        {
            Assert.Empty(LogManager.SelectFilesToDelete(Array.Empty<LogFileEntry>(), 1));
        }

        [Fact]
        public void SelectFilesToDelete_HandlesTotalsBeyondInt32()
        {
            var files = new[] { File("a", 3000 * MB, 2), File("b", 3000 * MB, 1) };

            var selected = LogManager.SelectFilesToDelete(files, 4096 * MB);

            Assert.Equal(new[] { "a" }, Names(selected));
        }
    }
}
