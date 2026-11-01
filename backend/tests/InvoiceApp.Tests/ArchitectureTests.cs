using InvoiceApp.Domain;
using NetArchTest.Rules;
using Xunit;

public class ArchitectureTests
{
    [Fact]
    public void Domain_does_not_depend_on_web_or_storage()
    {
        var result = Types.InAssembly(typeof(ApprovalService).Assembly)
            .ShouldNot()
            .HaveDependencyOnAny(
                "Microsoft.AspNetCore",          
                "InvoiceApp.Api",               
                "Microsoft.EntityFrameworkCore", // database (future)
                "Microsoft.Data.SqlClient")      // database (future)
            .GetResult();

        Assert.True(result.IsSuccessful,
            "Domain types with forbidden dependencies: " +
            string.Join(", ", result.FailingTypeNames ?? new List<string>()));
    }
}