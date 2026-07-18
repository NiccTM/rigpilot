using System.Collections.Specialized;
using PCHelper.App;

namespace PCHelper.Integration.Tests;

public sealed class BatchedObservableCollectionTests
{
    [Fact]
    public void KeyedSynchronizePublishesOneResetAndRetainsEqualItems()
    {
        BatchedObservableCollection<Row> collection = new();
        Row alpha = new("alpha", 1);
        Row beta = new("beta", 1);
        collection.SynchronizeByKey([alpha, beta], row => row.Id, StringComparer.Ordinal);
        List<NotifyCollectionChangedAction> notifications = [];
        collection.CollectionChanged += (_, args) => notifications.Add(args.Action);

        using (collection.DeferNotifications())
        {
            collection.SynchronizeByKey(
                [new Row("beta", 2), alpha, new Row("gamma", 1)],
                row => row.Id,
                StringComparer.Ordinal);
        }

        Assert.Equal([NotifyCollectionChangedAction.Reset], notifications);
        Assert.Equal(["beta", "alpha", "gamma"], collection.Select(row => row.Id));
        Assert.Equal(2, collection[0].Value);
        Assert.Same(alpha, collection[1]);
    }

    [Fact]
    public void EqualKeyedSnapshotPublishesNoCollectionNotification()
    {
        BatchedObservableCollection<Row> collection = new();
        Row[] snapshot = [new("alpha", 1), new("beta", 2)];
        collection.SynchronizeByKey(snapshot, row => row.Id, StringComparer.Ordinal);
        int notifications = 0;
        collection.CollectionChanged += (_, _) => notifications++;

        collection.SynchronizeByKey(snapshot.ToArray(), row => row.Id, StringComparer.Ordinal);

        Assert.Equal(0, notifications);
    }

    private sealed record Row(string Id, int Value);
}
