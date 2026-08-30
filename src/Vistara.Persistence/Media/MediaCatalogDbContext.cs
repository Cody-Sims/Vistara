using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Vistara.Persistence.Media;

public sealed class MediaCatalogDbContext(
    DbContextOptions<MediaCatalogDbContext> options) : DbContext(options)
{
    internal DbSet<PublicDerivativeRouteRow> PublicDerivativeRoutes =>
        Set<PublicDerivativeRouteRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        MediaPersistenceContributor.ConfigurePublicRoutes(
            modelBuilder.Entity<PublicDerivativeRouteRow>());
        var converter = new ValueConverter<DateTimeOffset, DateTime>(
            value => value.UtcDateTime,
            value => new DateTimeOffset(
                DateTime.SpecifyKind(value, DateTimeKind.Utc)));
        foreach (var property in modelBuilder.Entity<PublicDerivativeRouteRow>()
                     .Metadata.GetProperties())
        {
            property.SetColumnName(ToSnakeCase(property.Name));
            if (property.ClrType == typeof(DateTimeOffset))
            {
                property.SetValueConverter(converter);
            }
        }
    }

    private static string ToSnakeCase(string value)
    {
        var builder = new StringBuilder(value.Length + 8);
        for (int index = 0; index < value.Length; index++)
        {
            char character = value[index];
            if (index > 0 && char.IsUpper(character))
            {
                builder.Append('_');
            }

            builder.Append(char.ToLowerInvariant(character));
        }

        return builder.ToString();
    }
}
