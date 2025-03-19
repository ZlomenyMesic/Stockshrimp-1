﻿using Stockshrimp_1.evaluation;
using Stockshrimp_1.movegen;
using Stockshrimp_1.search.movesort;

#nullable enable
namespace Stockshrimp_1.search {
    internal static class OldPVS {

        // maximum depth allowed in the quiescence search itself
        private const int MAX_QSEARCH_DEPTH = 8;

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

        internal static bool Abort => total_nodes >= max_nodes;

        // increase the depth and do a re-search
        internal static void SearchDeeper() {
            cur_depth++;

            // as already mentioned, this represents the absolute depth limit
            cur_max_qsearch_depth = cur_depth + 8;

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

        // first search the transposition table for the score, if it's not there
        // just continue the regular search. parameters need to be the same as in the search method itself
        private static (short Score, Move[] PV) SearchTT(Board b, int ply, int depth, Window window) {

            // did we find the score?
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
            if (Abort)
                return (0, []);

            // we reached depth = 0, we evaluate the leaf node though the qsearch
            if (depth <= 0)
                return (QSearch(b, ply, window), []);

            // this gets incremented only if no qsearch, otherwise the node would count twice
            total_nodes++;

            int col = b.side_to_move;

            // is the color to play currently in check?
            bool is_checked = Movegen.IsKingInCheck(b, col);

            // NULL MOVE PRUNING:
            // we assume that there is at least one move that improves our position,
            // so we play a "null move", which is essentially no move at all (we just
            // flip the side to move and erase the en passant square). we then search
            // this null child at a reduced depth (depth reduce R). if we still fail
            // high despite skipping a move, we can expect that playing a move would
            // also fail high, and thus, we can prune this branch.
            //
            // NMP for this reason failes in zugzwangs
            //
            // avoid NMP for a few plys if we found a mate in the previous iteration
            // we must either find the shortest mate or escape. we also don't prune
            // if we are being checked
            if (!Eval.IsMateScore(pv_score) 
                && ply >= NMP.MIN_PLY 
                && depth >= NMP.MIN_DEPTH 
                && !is_checked 
                && window.CanFailHigh(col)) {

                // null window around beta
                Window nullw_beta = window.GetUpperBound(col);

                // child with no move played
                Board null_child = b.GetNullChild();

                int R = NMP.R;

                // do the reduced search
                short score = SearchTT(null_child, ply + 1, depth - R - 1, nullw_beta).Score;

                // if we failed high, that means the score is above beta and is "too good" to be
                // allowed by the opponent. if we don't fail high, we just continue the expansion
                if (window.FailsHigh(score, col))

                    // currently we are returning the null search score, but returning beta
                    // may also work. this needs some testing
                    return (score, []);
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
                // only expanded a single node so far
                // (captured a piece) maybe add???
                // just escaped a check
                // are checking the opposite king
                bool interesting = exp_nodes == 1 
                    || is_checked 
                    || Movegen.IsKingInCheck(child, col == 0 ? 1 : 0);

                // FUTILITY PRUNING:
                // we try to discard moves near the leaves which have no potential of raising alpha.
                // futility margin represents the largest possible score gain through a single move.
                // if we add this margin to the static eval of the position and still don't raise
                // alpha, we can prune this branch. we assume there probably isn't a phenomenal move
                // that could save this position
                if (ply >= FP.MIN_PLY 
                    && depth <= FP.MAX_DEPTH 
                    && !interesting) {

                    // as taken from chessprogrammingwiki:
                    // "If at depth 1 the margin does not exceed the value of a minor piece, at
                    // depth 2 it should be more like the value of a rook."
                    //
                    // however, a lower margin increases the search speed and thus our futility margin stays low
                    //
                    // TODO - BETTER FUTILITY MARGIN?
                    int margin = FP.GetMargin(depth, col, true);

                    // if we fail low, we fell under alpha. this means we already know of a better
                    // alternative somewhere else in the search tree, and we can prune this branch.
                    if (window.FailsLow((short)(Eval.StaticEval(child) + margin), col))
                        continue;
                }

                // LATE MOVE REDUCTIONS (LMR):
                // moves other than the pv node are expected to fail low (not raise alpha),
                // so we first search them with null window around alpha. if it does not fail
                // low as expected, we do a full re-search
                if (ply >= LMR.MIN_PLY 
                    && depth >= LMR.MIN_DEPTH 
                    && exp_nodes >= LMR.MIN_EXP_NODES) {

                    // depth reduce is larger with bad history
                    int R = interesting ? 0 : (History.GetRep(child, moves[i]) < -1320 ? 4 : 3);

                    // null window around alpha
                    Window nullw_alpha = window.GetLowerBound(col);

                    // once again a reduced depth search
                    int score = SearchTT(child, ply + 1, depth - R - 1, nullw_alpha).Score;

                    // we failed low, we prune this branch. it is not good enough
                    if (window.FailsLow((short)score, col))
                        continue;
                }

                // if we got through all the pruning all the way to this point,
                // we expect this move to raise alpha, so we search it at full depth
                (short Score, Move[] PV) full_search = SearchTT(child, ply + 1, depth - 1, window);

                // we somehow still failed low
                if (window.FailsLow((short)full_search.Score, col)) {

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

        private static short QSearch(Board position, int ply, Window window) {
            ++OldPVS.total_nodes;
            if (OldPVS.Abort)
                return 0;
            if (ply > OldPVS.achieved_depth)
                OldPVS.achieved_depth = ply;
            if (ply >= OldPVS.cur_max_qsearch_depth)
                return Eval.StaticEval(position);
            int sideToMove = position.side_to_move;
            bool flag = Movegen.IsKingInCheck(position, sideToMove);
            if (!flag) {
                int score = Eval.StaticEval(position);
                if (window.TryCutoff((short)score, sideToMove))
                    return window.GetBoundScore(sideToMove);
            }
            List<Move> moveList = Movegen.GetLegalMoves(position);
            if (!flag && moveList.Count == 0)
                return 0;
            if (!flag) {
                List<Move> capts = new List<Move>();
                for (int index = 0; index < moveList.Count; ++index) {
                    if (moveList[index].Capture() != 6)
                        capts.Add(moveList[index]);
                }
                moveList = MVV_LVA.SortCaptures(capts);
            }
            int num = 0;
            for (int index = 0; index < moveList.Count; ++index) {
                Board position1 = position.Clone();
                position1.DoMove(moveList[index]);
                ++num;
                int score = OldPVS.QSearch(position1, ply + 1, window);
                if (window.TryCutoff((short)score, sideToMove))
                    break;
            }
            return num == 0 & flag ? Eval.GetMateScore(sideToMove, ply) : window.GetBoundScore(sideToMove);
        }

        private static Move[] AddMoveToPV(Move move, Move[] pv) {
            Move[] destinationArray = new Move[pv.Length + 1];
            destinationArray[0] = move;
            Array.Copy((Array)pv, 0, (Array)destinationArray, 1, pv.Length);
            return destinationArray;
        }
    }
}
