using Application.Interfaces.Repositories;
using Application.Interfaces.Services;
using Infrastructure.Data;
using Infrastructure.Identity;
using Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Infrastructure.Extensions;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = ResolveConnectionString(configuration);

        services.AddDbContext<AppDbContext>(options =>
            options.UseSqlServer(
                connectionString,
                sqlServerOptions => sqlServerOptions.EnableRetryOnFailure(
                    maxRetryCount: 5,
                    maxRetryDelay: TimeSpan.FromSeconds(10),
                    errorNumbersToAdd: null)));

        services.AddScoped<IAuthRepository, AuthRepository>();
        services.AddScoped<IMasterDataRepository, MasterDataRepository>();
        services.AddScoped<ICustomerRepository, CustomerRepository>();
        services.AddScoped<ILoyaltySchemeRepository, LoyaltySchemeRepository>();
        services.AddScoped<INewInvoiceRepository, NewInvoiceRepository>();
        services.AddScoped<IRedemptionRepository, RedemptionRepository>();
        services.AddScoped<IProductRepository, ProductRepository>();
        services.AddScoped<IRoleRepository, RoleRepository>();
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IHrRepository, HrRepository>();
        services.AddScoped<ICityAssignmentRepository, CityAssignmentRepository>();
        services.AddScoped<IUserTargetRepository, UserTargetRepository>();
        services.AddScoped<IExpenseTypeRepository, ExpenseTypeRepository>();
        services.AddScoped<IExpenseRepository, ExpenseRepository>();
        services.AddScoped<IOrderRepository, OrderRepository>();
        services.AddScoped<IPasswordHasher, BCryptPasswordHasher>();
        services.AddScoped<ITokenService, JwtTokenService>();

        return services;
    }

    private static string ResolveConnectionString(IConfiguration configuration)
    {
        var directConnectionString = Environment.GetEnvironmentVariable("KSB_PR_CONNECTION")
            ?? Environment.GetEnvironmentVariable("SQLSERVER_CONNECTIONSTRING")
            ?? Environment.GetEnvironmentVariable("AZURE_SQL_CONNECTIONSTRING");
        if (!string.IsNullOrWhiteSpace(directConnectionString))
        {
            return directConnectionString;
        }

        return configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("ConnectionStrings:DefaultConnection is not configured.");
    }
}
