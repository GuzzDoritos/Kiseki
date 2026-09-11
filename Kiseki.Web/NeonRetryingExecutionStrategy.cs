using Microsoft.EntityFrameworkCore.Storage;

namespace Kiseki.Web;

public class NeonRetryingExecutionStrategy(ExecutionStrategyDependencies dependencies)
    : Kiseki.Core.Services.NeonRetryingExecutionStrategy(dependencies);
