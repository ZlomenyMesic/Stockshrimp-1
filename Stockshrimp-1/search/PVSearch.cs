﻿/*
 *  Stockshrimp chess engine 1.0
 *  developed by ZlomenyMesic
 */

using Stockshrimp_1.evaluation;
using Stockshrimp_1.movegen;
using Stockshrimp_1.search.movesort;
using Stockshrimp_1.search.pruning;
using System.Diagnostics;

#nullable enable
namespace Stockshrimp_1.search {
    internal static class PVSearch {

        // maximum depth allowed in the quiescence search itself
        private const int MAX_QSEARCH_DEPTH = 10;

        // maximum depth total - qsearch and regular search combined
        // changes each iteration depending on pvsearch depth
        private static int cur_max_qsearch_depth = 0;

        // current regular search depth
        // increments by 1 each iteration in the deepening
        internal static int cur_depth;

        // highest achieved depth this iteration
        // this is also equal to the highest ply achieved
        internal static int achieved_depth = 0;

        // total nodes searched this iteration
        internal static long total_nodes;

        // limit for the amount of nodes allowed to be searched
        internal static long max_nodes = long.MaxValue;

        // evaluated final score of the principal variation
        internal static int pv_score = 0;

        // PRINCIPAL VARIATION
        // in pvsearch, the pv represents a variation (sequence of moves),
        // which the engine considers the best. each move in the pv represents
        // the (supposedly) best-scoring moves for both sides, so the first
        // pv node is also the move the engine is going to play
        internal static Move[] PV = [];

        internal static bool Abort => total_nodes >= max_nodes 
            || (PVSControl.sw ?? Stopwatch.StartNew()).ElapsedMilliseconds >= PVSControl.time_budget_ms;

        // increase the depth and do a re-search
        internal static void SearchDeeper() {
            cur_depth++;

            // as already mentioned, this represents the absolute depth limit
            cur_max_qsearch_depth = cur_depth + MAX_QSEARCH_DEPTH;

            // reset total nodes
            total_nodes = 0L;

            // create more space for killers on the new depth
            Killers.Expand(cur_depth);

            // decrease history values, as they shouldn't be as relevant now.
            // erasing them completely would, however, slow down the search
            History.Shrink();

            // store the pv from the previous iteration in tt
            // this should hopefully allow some faster lookups
            StorePVinTT(PV, cur_depth);

            // actual start of the search tree
            (pv_score, PV) = Search(Game.board, 0, cur_depth, Window.Infinite);
        }

        // completely reset everything
        internal static void Reset() {
            cur_max_qsearch_depth = 0;
            cur_depth = 0;
            achieved_depth = 0;
            total_nodes = 0L;
            pv_score = 0;
            PV = [];
            Killers.Clear();
            History.Clear();
            TT.Clear();
        }

        // stores the pv in the transposition table.
        // needs the starting depth in order to store trustworthy entries
        private static void StorePVinTT(Move[] pv, int depth) {
            Board b = Game.board.Clone();

            // loop all pv-nodes
            for (int i = 0; i < pv.Length; i++) {

                // store the pv-node
                TT.Store(b, --depth, i, Window.Infinite, (short)pv_score, pv[i]);

                // play along the pv to store corrent positions as well
                b.DoMove(pv[i]);
            }
        }

        // first check the transposition table for the score, if it's not there
        // just continue the regular search. parameters need to be the same as in the search method itself
        internal static (short Score, Move[] PV) ProbeTT(Board b, int ply, int depth, Window window) {

            // did we find the position and score?
            // we also need to check the ply, since too early tt lookups cause some serious blunders
            if (ply >= TT.MIN_PLY && TT.GetScore(b, depth, ply, window, out short tt_score))

                // only return the score, no pv
                return (tt_score, []);

            // in case the position is not yet stored, we fully search it and then store it
            (short Score, Move[] PV) search = Search(b, ply, depth, window);
            TT.Store(b, depth, ply, window, search.Score, search.PV.Length != 0 ? search.PV[0] : default);
            return search;
        }

        // finally the actual PVS algorithm
        //
        // (i could use the /// but i hate the looks)
        // ply starts at zero and increases each ply (no shit sherlock).
        // depth, on the other hand, starts at the highest value and decreases over time.
        // once we get to depth = 0, we drop into the qsearch. the search window contains 
        // the alpha and beta values, which are the pillars to this thing
        private static (short Score, Move[] PV) Search(Board b, int ply, int depth, Window window) {

            // either crossed the time budget or maximum nodes
            // we cannot abort the first iteration - no bestmove
            if (Abort && cur_depth > 1)
                return (0, []);

            // we reached depth = 0, we evaluate the leaf node though the qsearch
            if (depth <= 0)
                return (QSearch(b, ply, window), []);

            // if the position is saved as a 3-fold repetition draw, return 0.
            // we have to check at ply 2 as well to prevent a forced draw by the opponent
            if ((ply == 1 || ply == 2) && Game.draws.Contains(Zobrist.GetHash(b))) {
                return (0, []);
            }

            // this gets incremented only if no qsearch, otherwise the node would count twice
            total_nodes++;

            int col = b.side_to_move;

            // is the color to play currently in check?
            bool is_checked = Movegen.IsKingInCheck(b, col);

            // razoring
            if (!is_checked && ply >= 3 && depth == 4) {
                short q_eval = QSearch(b, 2, window.GetLowerBound(col));

                int margin = 165 * depth * (col == 0 ? 1 : -1);

                if (window.FailsLow((short)(q_eval + margin), col)) {
                    depth -= 2;
                    ply += 2;
                }
            }

            // are the conditions for nmp satisfied?
            if (NMP.CanPrune(depth, ply, is_checked, pv_score, window, col)) {

                // we try the reduced search and check for failing high
                if (NMP.TryPrune(b, depth, ply, window, col, out short score)) {

                    // we failed high - prune this branch
                    return (score, []);
                }
            }

            // all legal moves sorted from best to worst (only a guess)
            // first the tt bestmove, then captures sorted by MVV-LVA,
            // then killer moves and last quiet moves sorted by history
            List<Move> moves = MoveSort.GetSortedMoves(b, depth);

            // counter for expanded nodes
            int exp_nodes = 0;

            // pv continuation to be appended?
            Move[] pv = [];

            // loop the possible moves
            for (int i = 0; i < moves.Count; ++i) {
                exp_nodes++;

                // create a child board with the move played
                Board child = b.Clone();
                child.DoMove(moves[i]);

                // did this move capture a piece?
                bool is_capture = moves[i].Capture() != 6;

                // we save the moves as visited to the history table.
                // history only stores quiet moves - no captures
                if (!is_capture)
                    History.AddVisited(b, moves[i]);

                // if a position is "interesting", we avoid pruning and reductions
                // a child node is marked as interesting if we:
                //
                // 1 - only expanded a single node so far
                // 2 - (captured a piece) maybe add???
                // 3 - just escaped a check
                // 4 - are checking the opposite king
                bool interesting = exp_nodes == 1 
                    || is_checked 
                    //|| is_capture
                    || Movegen.IsKingInCheck(child, col == 0 ? 1 : 0);

                short s_eval = Eval.StaticEval(child);


                // have to meet certain conditions for fp
                if (FP.CanPrune(ply, depth, interesting)) {

                    // we check for failing low despite the margin
                    if (FP.TryPrune(depth, col, s_eval, window)) {

                        // prune this branch
                        continue;
                    }
                }

                // REVERSE FUTILITY PRUNING:
                // we also use reverse futility pruning - it's basically the same as fp but we subtract
                // the margin from the static eval and prune the branch if we still fail high
                if (ply >= RFPruning.MIN_PLY
                    && depth <= RFPruning.MAX_DEPTH
                    && !interesting) {

                    int rev_margin = RFPruning.GetMargin(depth, col, true);

                    // we failed high (above beta). our opponent already has an alternative which
                    // wouldn't allow this score to happen
                    if (window.FailsHigh((short)(s_eval - rev_margin), col))
                        continue;
                }

                // more conditions
                if (LMR.CanPruneOrReduce(ply, depth, exp_nodes, interesting)) {

                    (bool prune, bool reduce) = LMR.TryPrune(child, moves[i], ply, depth, col, window);

                    // we failed low - prune this branch completely
                    if (prune) continue;

                    // we failed low with a margin - only reduce, don't prune
                    if (reduce) {
                        depth -= 2;
                        ply += 2;
                    }
                }

                // if we got through all the pruning all the way to this point,
                // we expect this move to raise alpha, so we search it at full depth
                (short Score, Move[] PV) full_search = ProbeTT(child, ply + 1, depth - 1, window);

                // we somehow still failed low
                if (window.FailsLow(full_search.Score, col)) {

                    // decrease the move's reputation
                    History.DecreaseRep(b, moves[i], depth);
                }

                // we didn't fail low => we have a new best move for this position
                else {

                    // store the new move in tt
                    TT.Store(b, depth, ply, window, full_search.Score, moves[i]);

                    // append this move followed by the child's pv to the bigger pv
                    pv = AddMoveToPV(moves[i], full_search.PV);

                    // we try a beta cutoff?
                    if (window.TryCutoff(full_search.Score, col)) {

                        // we got a beta cutoff (alpha grew over beta).
                        // this means this move is really good

                        // is it quiet?
                        if (!is_capture) {

                            // if a quiet move caused a beta cutoff, we increase it's
                            // reputation in history and save it as a killer move on this depth
                            History.IncreaseRep(b, moves[i], depth);
                            Killers.Add(moves[i], depth);
                        }

                        // return the score
                        return (window.GetBoundScore(col), pv);
                    }
                }
            }

            return exp_nodes == 0 

                // we didn't expand any nodes - terminal node
                ? (is_checked 

                    // if we are checked this means we got mated (there are no legal moves)
                    ? Eval.GetMateScore(col, ply)

                    // if we aren't checked we return draw (stalemate)
                    : (short)0, []) 

                // return the score as usual
                : (window.GetBoundScore(col), pv);
        }

        // same idea as ProbeTT, but used in qsearch
        internal static short QProbeTT(Board b, int ply, Window window) {

            int depth = MAX_QSEARCH_DEPTH - ply - cur_depth;

            // did we find the position and score?
            if (ply >= cur_depth + 3 && TT.GetScore(b, depth, ply, window, out short tt_score))
                return tt_score;

            // if the position is not yet stored, we continue the qsearch and then store it
            short score = QSearch(b, ply, window);
            TT.Store(b, depth, ply, window, score, default);
            return score;
        }

        // QUIESCENCE SEARCH:
        // instead of immediately returning the static eval of leaf nodes in the main
        // search tree, we return a qsearch eval. qsearch is essentially just an extension
        // to the main search, but only expands captures or checks. this prevents falsely
        // evaluating positions where we can for instance lose a queen in the next move
        private static short QSearch(Board b, int ply, Window window) {

            if (Abort)
                return 0;

            total_nodes++;

            // this stores the highest achieved search depth in this iteration
            if (ply > achieved_depth)
                achieved_depth = ply;

            // we reached the end, we return the static eval
            if (ply >= cur_max_qsearch_depth)
                return Eval.StaticEval(b);

            int col = b.side_to_move;

            // is the side to move in check?
            bool is_checked = Movegen.IsKingInCheck(b, col);

            short stand_pat = 0;

            // can not use stand pat when in check
            if (!is_checked) {

                // stand pat is nothing more than a static eval
                stand_pat = Eval.StaticEval(b);

                // if the stand pat fails high, we can return it
                // if not, we use it as a lower bound (alpha)
                if (window.TryCutoff(stand_pat, col))
                    return window.GetBoundScore(col);
            }

            // from a certain point, we only generate captures
            bool only_captures = !is_checked || ply >= cur_max_qsearch_depth - 3;

            List<Move> moves = Movegen.GetLegalMoves(b, only_captures);

            if (moves.Count == 0) {

                // if we aren't checked, it means there just aren't
                // any more captures and we can return the stand pat
                // (we also might be in stalemate - FIX THIS)
                if (!is_checked) {
                    return stand_pat;
                }

                // if we are checked it's checkmate
                if (is_checked && !only_captures) {
                    return Eval.GetMateScore(col, ply);
                }

                if (is_checked && only_captures) {
                    return Movegen.GetLegalMoves(b, false).Count == 0 
                        ? Eval.GetMateScore(col, ply)
                        : (short)(stand_pat - (col == 0 ? 100 : -100));
                }
            }

            // we generate only captures when we aren't checked
            if (only_captures) {

                // sort the captures by MVV-LVA
                // (most valuable victim - least valuable aggressor)
                moves = MVV_LVA.SortCaptures(moves);
            } 

            for (int i = 0; i < moves.Count; ++i) {

                Board child = b.Clone();
                child.DoMove(moves[i]);

                // value of the piece we just captured
                int captured = only_captures ? EvalTables.Values[moves[i].Capture()] : 0;

                // DELTA PRUNING:
                //
                //
                if (only_captures && ply >= cur_depth + 4) {
                    int delta_margin = (cur_max_qsearch_depth - ply) * 81 * (col == 0 ? 1 : -1);

                    //Console.WriteLine(delta_margin);

                    if (window.FailsLow((short)(stand_pat + captured + delta_margin), col))
                        continue;
                }

                // full search
                short score = QSearch(child, ply + 1, window);

                if (window.TryCutoff(score, col))
                    break;
            }

            return window.GetBoundScore(col);
        }

        private static Move[] AddMoveToPV(Move move, Move[] pv) {
            Move[] new_pv = new Move[pv.Length + 1];
            new_pv[0] = move;
            Array.Copy(pv, 0, new_pv, 1, pv.Length);
            return new_pv;
        }
    }
}
