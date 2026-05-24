namespace DeepSeek_v4_for_VisualStudio.Tests.Unit.Models;

public class RagModelsTests
{
    #region RagSourceAttribute

    [Fact]
    public void RagSourceAttribute_Constructor_SetsCategoryAndDescription()
    {
        var attr = new RagSourceAttribute("file-read", "读取文件内容");

        attr.Category.Should().Be("file-read");
        attr.Description.Should().Be("读取文件内容");
    }

    [Fact]
    public void RagSourceAttribute_AllowsMultipleOnSameTarget()
    {
        var usage = (AttributeUsageAttribute)Attribute.GetCustomAttribute(
            typeof(RagSourceAttribute), typeof(AttributeUsageAttribute))!;

        usage.AllowMultiple.Should().BeTrue();
    }

    [Fact]
    public void RagSourceAttribute_CanTargetMethodAndClass()
    {
        var usage = (AttributeUsageAttribute)Attribute.GetCustomAttribute(
            typeof(RagSourceAttribute), typeof(AttributeUsageAttribute))!;

        usage.ValidOn.Should().HaveFlag(AttributeTargets.Method);
        usage.ValidOn.Should().HaveFlag(AttributeTargets.Class);
    }

    #endregion

    #region RagSearchResult

    [Fact]
    public void RagSearchResult_Defaults_AreSetCorrectly()
    {
        var result = new RagSearchResult();

        result.DocumentId.Should().BeEmpty();
        result.Title.Should().BeEmpty();
        result.Content.Should().BeEmpty();
        result.RelevanceScore.Should().Be(0.0);
        result.Source.Should().BeNull();
        result.Metadata.Should().BeEmpty();
    }

    [Fact]
    public void RagSearchResult_CanSetAllProperties()
    {
        var result = new RagSearchResult
        {
            DocumentId = "doc-001",
            Title = "API Specification",
            Content = "The API provides endpoints for user management...",
            RelevanceScore = 0.95,
            Source = "docs/api-spec.md",
            Metadata = new Dictionary<string, string>
            {
                { "author", "dev-team" },
                { "version", "2.0" },
            },
        };

        result.DocumentId.Should().Be("doc-001");
        result.Title.Should().Contain("API");
        result.Content.Should().Contain("endpoints");
        result.RelevanceScore.Should().BeApproximately(0.95, 0.001);
        result.Source.Should().Be("docs/api-spec.md");
        result.Metadata.Should().ContainKey("author");
        result.Metadata["version"].Should().Be("2.0");
    }

    [Fact]
    public void RagSearchResult_RelevanceScore_IsBetweenZeroAndOne()
    {
        var highScore = new RagSearchResult { RelevanceScore = 0.99 };
        var lowScore = new RagSearchResult { RelevanceScore = 0.01 };
        var zeroScore = new RagSearchResult { RelevanceScore = 0.0 };

        highScore.RelevanceScore.Should().BeLessThanOrEqualTo(1.0);
        lowScore.RelevanceScore.Should().BeGreaterThanOrEqualTo(0.0);
        zeroScore.RelevanceScore.Should().Be(0.0);
    }

    #endregion

    #region RagDocument

    [Fact]
    public void RagDocument_Defaults_AreSetCorrectly()
    {
        var doc = new RagDocument();

        doc.Id.Should().NotBeNullOrEmpty();
        doc.Id.Length.Should().Be(32); // Guid N format
        doc.Title.Should().BeEmpty();
        doc.Content.Should().BeEmpty();
        doc.SourcePath.Should().BeNull();
        doc.DocumentType.Should().Be("general");
        doc.AddedAt.Should().BeCloseTo(DateTime.Now, TimeSpan.FromMinutes(1));
        doc.Metadata.Should().BeEmpty();
    }

    [Fact]
    public void RagDocument_CanSetAllProperties()
    {
        var addedAt = new DateTime(2025, 6, 15, 10, 30, 0);
        var doc = new RagDocument
        {
            Id = "custom-id",
            Title = "User Authentication Module",
            Content = "public class AuthService { ... }",
            SourcePath = "src/services/AuthService.cs",
            DocumentType = "code",
            AddedAt = addedAt,
            Metadata = new Dictionary<string, string>
            {
                { "language", "csharp" },
            },
        };

        doc.Id.Should().Be("custom-id");
        doc.Title.Should().Contain("Authentication");
        doc.Content.Should().Contain("AuthService");
        doc.SourcePath.Should().EndWith("AuthService.cs");
        doc.DocumentType.Should().Be("code");
        doc.AddedAt.Should().Be(addedAt);
        doc.Metadata["language"].Should().Be("csharp");
    }

    [Fact]
    public void RagDocument_Id_IsUniqueByDefault()
    {
        var doc1 = new RagDocument();
        var doc2 = new RagDocument();

        doc1.Id.Should().NotBe(doc2.Id);
    }

    #endregion

    #region RagStats

    [Fact]
    public void RagStats_Defaults_AreZero()
    {
        var stats = new RagStats();

        stats.TotalDocuments.Should().Be(0);
        stats.TotalTokens.Should().Be(0);
    }

    [Fact]
    public void RagStats_CanTrackMetrics()
    {
        var stats = new RagStats
        {
            TotalDocuments = 150,
            TotalTokens = 50000,
            LastUpdated = new DateTime(2025, 6, 15),
        };

        stats.TotalDocuments.Should().Be(150);
        stats.TotalTokens.Should().Be(50000);
        stats.LastUpdated.Year.Should().Be(2025);
    }

    #endregion
}
