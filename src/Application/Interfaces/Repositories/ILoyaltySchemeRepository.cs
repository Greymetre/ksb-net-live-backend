using Application.DTOs.LoyaltySchemes;
using Domain.Entities;

namespace Application.Interfaces.Repositories;

public interface ILoyaltySchemeRepository
{
    Task<IReadOnlyCollection<LoyaltySchemeDto>> GetSchemesAsync(LoyaltySchemeFilterDto filter, CancellationToken cancellationToken);
    Task<LoyaltySchemeDto?> GetSchemeAsync(ulong id, CancellationToken cancellationToken);
    Task<LoyaltyScheme?> FindSchemeEntityAsync(ulong id, CancellationToken cancellationToken);
    Task<bool> SchemeCodeExistsAsync(string code, ulong? exceptId, CancellationToken cancellationToken);
    Task<string?> GetLastSchemeCodeAsync(string prefix, CancellationToken cancellationToken);
    Task<LoyaltySchemeDto> CreateSchemeAsync(LoyaltyScheme scheme, CancellationToken cancellationToken);
    Task<LoyaltySchemeDto> SaveSchemeAsync(LoyaltyScheme scheme, CancellationToken cancellationToken);
    Task<bool> DeleteSchemeAsync(LoyaltyScheme scheme, CancellationToken cancellationToken);
    Task<LoyaltySchemeOptionsDto> GetOptionsAsync(CancellationToken cancellationToken);

    /// <summary>Every live segment, family and product id, for checking what a scheme line
    /// names. Read in one pass because a line can name hundreds of products.</summary>
    Task<ProductMasterIdsDto> GetProductMasterIdsAsync(CancellationToken cancellationToken);

    /// <summary>Segment, family and product names by id, for showing an imported line.</summary>
    Task<ProductNameMapsDto> GetProductNamesAsync(CancellationToken cancellationToken);

    /// <summary>The segments and families the template lists, so a sheet can be filled in
    /// without looking an id up elsewhere.</summary>
    Task<IReadOnlyCollection<ProductReferenceDto>> GetProductSegmentsAsync(CancellationToken cancellationToken);
    Task<IReadOnlyCollection<ProductReferenceDto>> GetProductFamiliesAsync(CancellationToken cancellationToken);
    Task<IReadOnlyCollection<SchemeDealerOptionDto>> GetDealerOptionsAsync(CancellationToken cancellationToken);
}
