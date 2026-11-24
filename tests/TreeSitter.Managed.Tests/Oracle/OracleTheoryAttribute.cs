using Xunit;

namespace TreeSitter.Tests.Oracle;

/// <summary>
/// Like <see cref="TheoryAttribute"/>, but the test is skipped when the native
/// oracle library is not available. On CI (CI=true or CI=1) a missing library
/// fails the test instead of skipping it, because CI always builds the oracle.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class OracleTheoryAttribute : TheoryAttribute
{
    public OracleTheoryAttribute()
    {
        if (!NativeOracle.IsAvailable && !NativeOracle.IsCiEnvironment)
        {
            Skip = "Native oracle library not found. " +
                "Searched: " + string.Join(", ", NativeOracle.SearchedPaths) + ". " +
                "Build it with 'bash tools/build-oracle.sh' or set " +
                "TS_ORACLE_PATH to the directory containing " +
                "libts_oracle.so / ts_oracle.dll / libts_oracle.dylib.";
        }
        // On CI (CI=true/CI=1) the oracle is always built first, so a missing
        // library must not be skipped here: the test runs and NativeOracle
        // fails it with a clear message instead.
    }
}
