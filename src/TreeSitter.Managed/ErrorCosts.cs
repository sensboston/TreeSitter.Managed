// Port of tree-sitter v0.27.2 (3fb7be4): lib/src/error_costs.h.

namespace TreeSitter;

/// <summary>
/// Constants that rank parse versions during error recovery.
/// </summary>
internal static class ErrorCosts
{
    // ERROR_STATE
    internal const ushort ErrorState = 0;

    // ERROR_COST_PER_RECOVERY
    internal const uint ErrorCostPerRecovery = 500;

    // ERROR_COST_PER_MISSING_TREE
    internal const uint ErrorCostPerMissingTree = 110;

    // ERROR_COST_PER_SKIPPED_TREE
    internal const uint ErrorCostPerSkippedTree = 100;

    // ERROR_COST_PER_SKIPPED_LINE
    internal const uint ErrorCostPerSkippedLine = 30;

    // ERROR_COST_PER_SKIPPED_CHAR
    internal const uint ErrorCostPerSkippedChar = 1;
}
