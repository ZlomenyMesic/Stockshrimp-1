﻿//
// Kreveta chess engine by ZlomenyMesic
// started 4-3-2025
//

namespace Kreveta.search.pruning;

internal static class Razoring {
    internal const int MinPly = 3;
    internal const int Depth  = 4;

    private const int QSPly  = 2;
    private const int MarginBase = 165;

    internal static bool TryReduce(in Board board, int depth, Color col, Window window) {
        short qEval = PVSearch.QSearch(board, QSPly, col == Color.WHITE 
            ? new(window.Alpha, (short)(window.Alpha + 1)) 
            : new((short)(window.Beta - 1), window.Beta));

        int margin = MarginBase * depth * (col == Color.WHITE ? 1 : -1);

        int score = qEval + margin;
        return col == Color.WHITE
            ? (score <= window.Alpha)
            : (score >= window.Beta);
    }
}
