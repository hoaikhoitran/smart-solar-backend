using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using SmartSolar.Modules.Catalog.Entities;
using SmartSolar.Modules.PreSurvey.Entities;
using SmartSolar.Modules.SolarSimulation.Entities;

namespace SmartSolar.Tests.TestSupport;

/// <summary>
/// SQLite cannot ORDER BY or range-compare decimal and DateTimeOffset columns;
/// PostgreSQL can. For tests only, those columns on Product and SurveyRequest are
/// stored as REAL and INTEGER so catalog sorting/filtering and survey-request
/// queues run in the database as they do in production. Other entities keep the
/// production model.
/// </summary>
public sealed class SqliteCatalogModelCustomizer : ModelCustomizer
{
    public SqliteCatalogModelCustomizer(ModelCustomizerDependencies dependencies)
        : base(dependencies)
    {
    }

    public override void Customize(ModelBuilder modelBuilder, DbContext context)
    {
        base.Customize(modelBuilder, context);

        ConvertSortableColumns(modelBuilder.Entity<Product>());
        ConvertSortableColumns(modelBuilder.Entity<SurveyRequest>());
        ConvertSortableColumns(modelBuilder.Entity<SolarSimulation>());
    }

    private static void ConvertSortableColumns(EntityTypeBuilder entity)
    {
        foreach (var property in entity.Metadata.GetProperties())
        {
            var type = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;

            if (type == typeof(decimal))
            {
                property.SetValueConverter(new ValueConverter<decimal, double>(v => (double)v, v => (decimal)v));
                property.SetColumnType("REAL");
            }
            else if (type == typeof(DateTimeOffset))
            {
                // All these timestamps are UTC, so UTC ticks round-trip exactly.
                property.SetValueConverter(new ValueConverter<DateTimeOffset, long>(
                    v => v.UtcTicks, v => new DateTimeOffset(v, TimeSpan.Zero)));
                property.SetColumnType("INTEGER");
            }
        }
    }
}
