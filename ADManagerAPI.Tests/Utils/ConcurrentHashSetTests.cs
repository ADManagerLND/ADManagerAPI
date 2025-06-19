using ADManagerAPI.Utils;

namespace ADManagerAPI.Tests.Utils;

public class ConcurrentHashSetTests : IDisposable
{
    private ConcurrentHashSet<string> _hashSet;

    public ConcurrentHashSetTests()
    {
        _hashSet = new ConcurrentHashSet<string>();
    }

    public void Dispose()
    {
        _hashSet?.Dispose();
    }

    [Fact]
    public void Constructor_ShouldCreateEmptyHashSet()
    {
        // Assert
        _hashSet.Count.Should().Be(0);
    }

    [Fact]
    public void Constructor_WithCollection_ShouldInitializeWithItems()
    {
        // Arrange
        var items = new[] { "item1", "item2", "item3" };

        // Act
        using var hashSet = new ConcurrentHashSet<string>(items);

        // Assert
        hashSet.Count.Should().Be(3);
        hashSet.Contains("item1").Should().BeTrue();
        hashSet.Contains("item2").Should().BeTrue();
        hashSet.Contains("item3").Should().BeTrue();
    }

    [Fact]
    public void Add_ShouldAddUniqueItem()
    {
        // Act
        var result = _hashSet.Add("test");

        // Assert
        result.Should().BeTrue();
        _hashSet.Count.Should().Be(1);
        _hashSet.Contains("test").Should().BeTrue();
    }

    [Fact]
    public void Add_ShouldNotAddDuplicateItem()
    {
        // Arrange
        _hashSet.Add("test");

        // Act
        var result = _hashSet.Add("test");

        // Assert
        result.Should().BeFalse();
        _hashSet.Count.Should().Be(1);
    }

    [Fact]
    public void Contains_ShouldReturnTrueForExistingItem()
    {
        // Arrange
        _hashSet.Add("test");

        // Act
        var result = _hashSet.Contains("test");

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public void Contains_ShouldReturnFalseForNonExistingItem()
    {
        // Act
        var result = _hashSet.Contains("nonexistent");

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public void Remove_ShouldRemoveExistingItem()
    {
        // Arrange
        _hashSet.Add("test");

        // Act
        var result = _hashSet.Remove("test");

        // Assert
        result.Should().BeTrue();
        _hashSet.Count.Should().Be(0);
        _hashSet.Contains("test").Should().BeFalse();
    }

    [Fact]
    public void Remove_ShouldReturnFalseForNonExistingItem()
    {
        // Act
        var result = _hashSet.Remove("nonexistent");

        // Assert
        result.Should().BeFalse();
        _hashSet.Count.Should().Be(0);
    }

    [Fact]
    public void Clear_ShouldRemoveAllItems()
    {
        // Arrange
        _hashSet.Add("item1");
        _hashSet.Add("item2");
        _hashSet.Add("item3");

        // Act
        _hashSet.Clear();

        // Assert
        _hashSet.Count.Should().Be(0);
        _hashSet.Contains("item1").Should().BeFalse();
        _hashSet.Contains("item2").Should().BeFalse();
        _hashSet.Contains("item3").Should().BeFalse();
    }

    [Fact]
    public void ToArray_ShouldReturnAllItems()
    {
        // Arrange
        var items = new[] { "item1", "item2", "item3" };
        foreach (var item in items)
        {
            _hashSet.Add(item);
        }

        // Act
        var result = _hashSet.ToArray();

        // Assert
        result.Should().HaveCount(3);
        result.Should().Contain(items);
    }

    [Fact]
    public void GetEnumerator_ShouldReturnAllItems()
    {
        // Arrange
        var items = new[] { "item1", "item2", "item3" };
        foreach (var item in items)
        {
            _hashSet.Add(item);
        }

        // Act
        var result = new List<string>();
        var enumerator = _hashSet.GetEnumerator();
        while (enumerator.MoveNext())
        {
            result.Add(enumerator.Current);
        }

        // Assert
        result.Should().HaveCount(3);
        result.Should().Contain(items);
    }

    [Fact]
    public async Task ConcurrentAdd_ShouldHandleMultipleThreads()
    {
        // Arrange
        const int threadCount = 10;
        const int itemsPerThread = 100;
        var tasks = new List<Task>();

        // Act
        for (int i = 0; i < threadCount; i++)
        {
            int threadId = i;
            tasks.Add(Task.Run(() =>
            {
                for (int j = 0; j < itemsPerThread; j++)
                {
                    _hashSet.Add($"thread{threadId}_item{j}");
                }
            }));
        }

        await Task.WhenAll(tasks);

        // Assert
        _hashSet.Count.Should().Be(threadCount * itemsPerThread);
    }

    [Fact]
    public async Task ConcurrentReadWrite_ShouldNotThrowException()
    {
        // Arrange
        const int iterations = 1000;
        var addTask = Task.Run(() =>
        {
            for (int i = 0; i < iterations; i++)
            {
                _hashSet.Add($"item{i}");
            }
        });

        var readTask = Task.Run(() =>
        {
            for (int i = 0; i < iterations; i++)
            {
                var count = _hashSet.Count;
                var contains = _hashSet.Contains($"item{i}");
                var array = _hashSet.ToArray();
            }
        });

        // Act & Assert
        var act = async () => await Task.WhenAll(addTask, readTask);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task ConcurrentAddRemove_ShouldMaintainConsistency()
    {
        // Arrange
        const int iterations = 500;
        var items = Enumerable.Range(0, iterations).Select(i => $"item{i}").ToArray();

        // Pré-remplir avec des éléments
        foreach (var item in items)
        {
            _hashSet.Add(item);
        }

        var removeTask = Task.Run(() =>
        {
            for (int i = 0; i < iterations; i += 2) // Supprimer les éléments pairs
            {
                _hashSet.Remove($"item{i}");
            }
        });

        var addTask = Task.Run(() =>
        {
            for (int i = iterations; i < iterations * 2; i++) // Ajouter de nouveaux éléments
            {
                _hashSet.Add($"item{i}");
            }
        });

        // Act
        await Task.WhenAll(removeTask, addTask);

        // Assert
        // Vérifier que les éléments impairs originaux sont toujours là
        for (int i = 1; i < iterations; i += 2)
        {
            _hashSet.Contains($"item{i}").Should().BeTrue();
        }

        // Vérifier que les nouveaux éléments ont été ajoutés
        for (int i = iterations; i < iterations * 2; i++)
        {
            _hashSet.Contains($"item{i}").Should().BeTrue();
        }
    }
} 