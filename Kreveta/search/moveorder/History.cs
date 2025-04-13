﻿//
// Kreveta chess engine by ZlomenyMesic
// started 4-3-2025
//

using Kreveta.evaluation;
using Kreveta.movegen;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Kreveta.search.moveorder;

// to help move ordering, we use a few different history heuristics.
// the idea of these is that we save moves that proved to be good
// or bad in the past, idenependently of the position. for instance,
// if we noticed that sacrificing our rook two moves ago was a terrible
// move, we can usually be assured that doing the same two pawn pushes
// later would be the same exact blunder.
internal static class History {

    // the moves are usually indexed [from, to] but after some testing,
    // indexing by [from, piece] yields much better results. it may just
    // be a failure in my implementation, though.

    // this array stores history values of quiet moves - no captures
    [ReadOnly(true)]
    private static readonly int[,] QuietScores = new int[64, 12];

    private const int PawnCorrHistSize = 1048576;
    [ReadOnly(true)]
    private static readonly int[,] PawnCorrHist = new int[2, PawnCorrHistSize];

    // butterfly boards store the number of times a move has been visited.
    //
    // the main disadvantage of the history heuristic is that it tends to
    // bias towards moves which appear more frequently. there might be
    // a move that turned out to be very good, but doesn't occur that
    // often and thus, doesn't get a very large score.
    // (taken from Chess Programming Wiki)
    // 
    // Mark. W then proposed the idea of relative history heuristics,
    // which take into consideration the number of occurences as well.
    //
    // the presented idea is that SCORE = HH_SCORE / BF_SCORE
    //
    // after some more testing though, it appears that this assumtion
    // is quite wrong (or at least in my particular case), and not only
    // that, the engine performs better when i do the exact opposite,
    // so the history value is multiplied by the common log of bf score
    [ReadOnly(true)]
    private static readonly int[,] ButterflyScores = new int[64, 12];

    [ReadOnly(true)]
    private const int RelHHScale = 12;

    // before each new iterated depth, we "shrink" the stored values.
    // they are still quite relevant, but the new values coming are
    // more important, so we want them to have a stronger effect
    internal static void Shrink() {
        for (int i = 0; i < 64; i++) {
            for (int j = 0; j < 12; j++) {

                // history reputation is straightforward
                QuietScores[i, j] /= 2;

                // this for an unexplainable reason works
                // out to be the best option available
                ButterflyScores[i, j] = Math.Min(1, ButterflyScores[i, j]);
            }
        }

        for (int i = 0; i < PawnCorrHistSize; i++) {
            PawnCorrHist[0, i] = 0;
            PawnCorrHist[1, i] = 0;
        }
    }

    // clears all history data
    internal static void Clear() {
        Array.Clear(QuietScores);
        Array.Clear(ButterflyScores);
        Array.Clear(PawnCorrHist);
    }

    // increases the history rep of a quiet move
    internal static void IncreaseQRep([NotNull] in Board board, [NotNull] Move move, int depth) {
        int i = PieceIndex(board, move);
        int end = move.End;

        QuietScores[end, i] += QuietShift(depth);

        // add the move as visited, too
        ButterflyScores[end, i]++;
    }

    // decreases the history rep of a quiet move
    internal static void DecreaseQRep([NotNull] in Board board, [NotNull] Move move, int depth) {
        int i = PieceIndex(board, move);
        int end = move.End;

        QuietScores[end, i] -= QuietShift(depth);

        // also the same, add the move as visited
        ButterflyScores[end, i]++;
    }

    // calculate the reputation of a move
    internal static int GetRep([NotNull] in Board board, [NotNull] Move move) {
        int i = PieceIndex(board, move);
        int end = move.End;

        // quiet score and butterfly score
        int q  = QuietScores[end, i];
        int bf = ButterflyScores[end, i];

        if (bf == 0) return 0;

        // as already mentioned, we do the opposite of what relative
        // history heuristics is about, and we multiply the score
        // by the common log of the bf score.

        // the idea isn't random though. if a move has occured
        // many times and still managed to keep a positive score,
        // it was probably good in most of the cases, which means
        // it is likely to be good in the next case as well. on
        // the other hand, we might have a move that only occured
        // a few times and also got a positive score, but it could
        // have been just something specific to the position, and
        // overall the move is terrible.

        // so this is still relative to the butterfly boards, but
        // we assume that with a larger amount of encounters, the
        // score is more "confirmed" than with just a few cases
        return RelHHScale * q / bf;
    }

    // calculate the index of a piece in the boards
    // (we just add 6 for white pieces)
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int PieceIndex([NotNull] in Board board, [NotNull] Move move) {

        PType piece = move.Piece;
        Color col = (board.Pieces[(byte)Color.WHITE, (byte)piece] ^ Consts.SqMask[move.Start]) == 0
            ? Color.BLACK
            : Color.WHITE;

        return (byte)piece + (col == Color.WHITE ? 6 : 0);
    }

    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    private const int QuietShiftSubtract = 5;

    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    private const int QuietShiftLimit    = 84;

    // how much should a move affect the history reputation.
    // i borrowed this idea from somewhere and forgot where,
    // but it turns out to be very precise
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int QuietShift(int depth)
        => Math.Min(depth * depth - QuietShiftSubtract, QuietShiftLimit);

    internal static void UpdatePawnCorrHist([NotNull] in Board board, int score, int depth) {
        if (depth <= 2) return;

        ulong wHash = Zobrist.GetPawnHash(board, Color.WHITE);
        ulong bHash = Zobrist.GetPawnHash(board, Color.BLACK);

        int wIndex = (int)(wHash % PawnCorrHistSize);
        int bIndex = (int)(bHash % PawnCorrHistSize);

        short staticEval = Eval.StaticEval(board);
        int diff = Math.Abs(score - staticEval);
        int shift = PawnCorrHistShift(diff, depth);

        //Console.WriteLine($"{depth} static {staticEval} score {score} diff {diff} shift {shift}");
        //Console.WriteLine($"{score} {staticEval} white {(score > staticEval ? shift : -shift)} black {(score > staticEval ? -shift : shift)}");

        if (shift == 0) return;

        //Console.WriteLine(shift);

        PawnCorrHist[(byte)Color.WHITE, wIndex] += score > staticEval ? shift : -shift;
        PawnCorrHist[(byte)Color.BLACK, bIndex] += score > staticEval ? -shift : shift;

        PawnCorrHist[(byte)Color.WHITE, wIndex] = Math.Min(2048, Math.Max(PawnCorrHist[(byte)Color.WHITE, wIndex], -2048));
        PawnCorrHist[(byte)Color.BLACK, bIndex] = Math.Min(2048, Math.Max(PawnCorrHist[(byte)Color.BLACK, bIndex], -2048));
    }

    internal static int GetPawnCorrection([NotNull] in Board board) {
        ulong wHash = Zobrist.GetPawnHash(board, Color.WHITE);
        ulong bHash = Zobrist.GetPawnHash(board, Color.BLACK);

        int wIndex = (int)(wHash % PawnCorrHistSize);
        int bIndex = (int)(bHash % PawnCorrHistSize);

        int correction = (PawnCorrHist[(byte)Color.WHITE, wIndex] + PawnCorrHist[(byte)Color.BLACK, bIndex]) / 128;
        return correction;
    }

    private static int PawnCorrHistShift(int diff, int depth) {
        return Math.Min(12, diff * (depth - 2) / 256);
    }
}
