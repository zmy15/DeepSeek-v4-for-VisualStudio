namespace DeepSeek_v4_for_VisualStudio.Tests.Integration;

public class ChatPersistenceServiceTests
{
    private readonly string _testSolutionPath = @"F:\TestProjects\TestSolution\Test.sln";

    [Fact]
    public void GetStoragePath_WithSolutionPath_ReturnsHashedPath()
    {
        var path = ChatPersistenceService.GetStoragePath(_testSolutionPath);

        path.Should().NotBeNullOrEmpty();
        path.Should().Contain("DeepSeekVS");
        path.Should().Contain("conversations");
        path.Should().EndWith(".json");
    }

    [Fact]
    public void GetStoragePath_NullPath_ReturnsUnsavedPath()
    {
        var path = ChatPersistenceService.GetStoragePath(null);

        path.Should().Contain("_unsaved.json");
    }

    [Fact]
    public void GetStoragePath_EmptyPath_ReturnsUnsavedPath()
    {
        var path = ChatPersistenceService.GetStoragePath("");

        path.Should().Contain("_unsaved.json");
    }

    [Fact]
    public void GetStoragePath_SameSolutionPath_ReturnsSameHash()
    {
        var path1 = ChatPersistenceService.GetStoragePath(_testSolutionPath);
        var path2 = ChatPersistenceService.GetStoragePath(_testSolutionPath);

        path1.Should().Be(path2);
    }

    [Fact]
    public void GetStoragePath_DifferentPaths_ReturnsDifferentFiles()
    {
        var path1 = ChatPersistenceService.GetStoragePath(@"C:\ProjectA\A.sln");
        var path2 = ChatPersistenceService.GetStoragePath(@"C:\ProjectB\B.sln");

        path1.Should().NotBe(path2);
    }

    [Fact]
    public void LoadSessions_NonExistentFile_ReturnsEmptyContainer()
    {
        // 使用一个不可能存在的路径
        var nonExistentPath = @"Z:\NonExistent\Project.sln";

        var container = ChatPersistenceService.LoadSessions(nonExistentPath);

        container.Should().NotBeNull();
        container.Sessions.Should().NotBeNull();
    }

    [Fact]
    public void LoadSessions_NullPath_ReturnsEmptyContainer()
    {
        var container = ChatPersistenceService.LoadSessions(null);

        container.Should().NotBeNull();
        container.Sessions.Should().NotBeNull();
        container.SolutionPath.Should().Be("(unsaved)");
    }

    [Fact]
    public void SaveAndLoad_RoundTrip_Works()
    {
        var testPath = string.Format(@"F:\TestData\RoundTrip{0}\Test.sln", Guid.NewGuid().ToString("N").Substring(0, 8));
        var original = new SessionsContainer
        {
            SolutionPath = testPath,
            Sessions = new List<ChatSession>
            {
                new()
                {
                    Id = "session-1",
                    Title = "Test Session",
                    CreatedAt = new DateTime(2026, 5, 15),
                    LastActiveAt = new DateTime(2026, 5, 15),
                }
            },
            ActiveSessionId = "session-1",
        };

        // Act: 保存
        ChatPersistenceService.SaveSessions(testPath, original);

        // Act: 加载
        var loaded = ChatPersistenceService.LoadSessions(testPath);

        // Assert
        loaded.Should().NotBeNull();
        loaded.Sessions.Should().HaveCount(1);
        loaded.Sessions[0].Id.Should().Be("session-1");
        loaded.Sessions[0].Title.Should().Be("Test Session");
        loaded.ActiveSessionId.Should().Be("session-1");

        // Cleanup
        ChatPersistenceService.DeleteAllSessions(testPath);
    }

    [Fact]
    public void SaveSessions_NullContainer_DoesNothing()
    {
        var testPath = @"F:\TestData\NullContainer\Test.sln";

        var act = () => ChatPersistenceService.SaveSessions(testPath, null!);

        act.Should().NotThrow();
    }

    [Fact]
    public void DeleteAllSessions_RemovesFile()
    {
        var testPath = string.Format(@"F:\TestData\Delete{0}\Test.sln", Guid.NewGuid().ToString("N").Substring(0, 8));
        var container = new SessionsContainer
        {
            SolutionPath = testPath,
            Sessions = new List<ChatSession>
            {
                new() { Id = "to-delete", Title = "Will be deleted" }
            }
        };

        // 先保存
        ChatPersistenceService.SaveSessions(testPath, container);

        // 删除
        ChatPersistenceService.DeleteAllSessions(testPath);

        // 重新加载应返回空容器
        var reloaded = ChatPersistenceService.LoadSessions(testPath);
        reloaded.Sessions.Should().BeEmpty();
    }

    [Fact]
    public void DeleteAllSessions_NonExistentFile_DoesNotThrow()
    {
        var act = () => ChatPersistenceService.DeleteAllSessions(@"Z:\Never\Existed.sln");

        act.Should().NotThrow();
    }
}
