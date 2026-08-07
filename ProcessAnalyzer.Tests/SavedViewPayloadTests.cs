using ProcessAnalyzer.Web.Sync;
using Xunit;

namespace ProcessAnalyzer.Tests;

/// <summary>
/// What a saved view is allowed to tell us about somebody, and what it must not.
/// </summary>
/// <remarks>
/// The payload is a whole screen state and most of it says nothing about anybody's work. Two things do: what the view
/// narrows by, and what it keeps on screen. Everything else — scroll offsets, page ids, card modes — is noise, and one
/// piece of it is worse than noise: the filter VALUES a person typed are customer names and licence plates.
/// <para>
/// The source grant is necessarily wider than the use, because SQL Server cannot grant half a column. So the restraint
/// lives here, and a test has to hold it: the first case below is the one that would otherwise be quietly lost in a
/// later refactor.
/// </para>
/// </remarks>
public sealed class SavedViewPayloadTests
{
    /// <summary>The real shape, with the values replaced — groups, fixed-left columns, filters with typed text.</summary>
    private const string RealisticPayload = """
        {
          "scrollX": [0], "scrollY": 0, "pageId": "0", "rowId": 0,
          "dataSourceId": "primary", "searchWord": "", "folderId": 0,
          "markedOnly": false, "mode": "Table", "cardMode": "Read",
          "definition": {
            "Filters": [
              { "Type": "Filter", "PropertyName": "Carrier.Vehicle.Code", "Value": "*111*|*222*" },
              { "PropertyName": "FromDateTime", "Value": "2026-01-01" },
              { "PropertyName": "Supplier.Agent.Code", "Value": "69" }
            ],
            "Groups": [
              {
                "Order": 0, "Parent": null, "Type": "Group",
                "GroupType": "ColumnsFixedLeft", "Orientation": "Horizontal",
                "Content": [
                  { "PropertyName": "Marker.Name" },
                  { "PropertyName": "PlannedDate" }
                ]
              },
              {
                "Order": 1, "Type": "Group", "Orientation": "Horizontal",
                "Content": [
                  { "PropertyName": "ActualDate" },
                  {
                    "Type": "Group",
                    "Content": [{ "PropertyName": "EstimatedCost" }]
                  }
                ]
              }
            ]
          }
        }
        """;

    [Fact]
    public void NoFilterValueSurvivesTheRead()
    {
        var (filters, columns) = SavedViewPayload.Decompose(RealisticPayload);

        // The values in the fixture are what a person typed. None of them may appear anywhere in the result — this is
        // the one assertion that keeps customer data out of the analytical store.
        var everything = filters.Concat(columns).ToList();
        Assert.DoesNotContain("*111*|*222*", everything);
        Assert.DoesNotContain("2026-01-01", everything);
        Assert.DoesNotContain("69", everything);
    }

    [Fact]
    public void FilterPropertiesAreRead()
    {
        var (filters, _) = SavedViewPayload.Decompose(RealisticPayload);

        Assert.Equal(["Carrier.Vehicle.Code", "FromDateTime", "Supplier.Agent.Code"], filters);
    }

    [Fact]
    public void ColumnsAreReadOutOfNestedGroupsInOrder()
    {
        var (_, columns) = SavedViewPayload.Decompose(RealisticPayload);

        // Nested to three levels in the fixture, and the order is the order on screen — which is what makes two
        // people's layouts comparable at all.
        Assert.Equal(["Marker.Name", "PlannedDate", "ActualDate", "EstimatedCost"], columns);
    }

    [Fact]
    public void AFilteredPropertyIsNotCountedAsAVisibleColumn()
    {
        var (_, columns) = SavedViewPayload.Decompose(RealisticPayload);

        // Filters carry PropertyName too. Reading them as columns would make every narrowed property look like one
        // somebody keeps on screen, and the two say different things about a role.
        Assert.DoesNotContain("Carrier.Vehicle.Code", columns);
        Assert.DoesNotContain("FromDateTime", columns);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("{\"definition\": 42}")]
    [InlineData("{\"noDefinition\": true}")]
    public void APayloadItCannotReadCostsOneViewAndNotThePull(string? payload)
    {
        // Never throws: one unreadable view is one missing signature, an exception would cost the whole pull, and the
        // whole pull is the thing that answers the question.
        var (filters, columns) = SavedViewPayload.Decompose(payload);

        Assert.Empty(filters);
        Assert.Empty(columns);
    }

    [Fact]
    public void ARepeatedPropertyIsCountedOnce()
    {
        var (filters, columns) = SavedViewPayload.Decompose(
            """
            {
              "definition": {
                "Filters": [
                  { "PropertyName": "Type", "Value": "a" },
                  { "PropertyName": "Type", "Value": "b" }
                ],
                "Groups": [
                  { "Content": [{ "PropertyName": "Name" }, { "PropertyName": "Name" }] }
                ]
              }
            }
            """
        );

        // A view can narrow the same property twice. Counting it twice would inflate the person's signature and make a
        // narrow role look like a broad one.
        Assert.Equal(["Type"], filters);
        Assert.Equal(["Name"], columns);
    }
}
