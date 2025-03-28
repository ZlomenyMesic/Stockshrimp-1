﻿/*
 *  Stockshrimp chess engine 1.0
 *  developed by ZlomenyMesic
 */

using Stockshrimp_1.evaluation;
using System.Runtime.CompilerServices;

namespace Stockshrimp_1.search.pruning;

// NULL MOVE PRUNING
// we assume that there is at least one move that improves our position,
// so we play a "null move", which is essentially no move at all (we just
// flip the side to move and erase the en passant square). we then search
// this null child at a reduced depth (depth reduce R). if we still fail
// high despite skipping a move, we can expect that playing a move would
// also fail high, and thus, we can prune this branch.
//
// NMP for this reason failes in zugzwangs
internal static class NMP {

    // minimum depth and ply required for nmp
    internal const int MIN_DEPTH = 0;
    internal const int MIN_PLY = 2;

    // depth reduce base within nmp
    internal const int R_Base = 3;

    // can we try nmp in a position?
    // avoid NMP for a few plys if we found a mate in the previous iteration
    // we must either find the shortest mate or escape. we also don't prune
    // if we are being checked
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool CanPrune(int depth, int ply, bool is_checked, int pv_score, Window window, int col) {

        // are we at a permissible depth and ply
        return depth >= MIN_DEPTH
            && ply >= MIN_PLY

            // do NOT prune when in check
            && !is_checked

            // do not prune when previous search iteration found a mate
            && !Eval.IsMateScore(pv_score)

            // there is "space" to fail high in the window
            && window.CanFailHigh(col);
    }

    // try null move pruning
    internal static bool TryPrune(Board b, int depth, int ply, Window window, int col, out short score) {

        // null window around beta
        Window nullw_beta = window.GetUpperBound(col);

        // child with no move played
        Board null_child = b.GetNullChild();

        int R = ply <= 4 ? R_Base - 1 : R_Base;

        // do the reduced search
        score = PVSearch.ProbeTT(null_child, ply + 1, depth - R - 1, nullw_beta).Score;

        // if we failed high, that means the score is above beta and is "too good" to be
        // allowed by the opponent. if we don't fail high, we just continue the expansion
        //
        // currently we are returning the null search score, but returning beta
        // may also work. this needs some testing
        return window.FailsHigh(score, col);
    }
}
