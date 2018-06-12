﻿// Copyright (c) 2014 Daniel Grunwald
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy of this
// software and associated documentation files (the "Software"), to deal in the Software
// without restriction, including without limitation the rights to use, copy, modify, merge,
// publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons
// to whom the Software is furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in all copies or
// substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
// INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
// PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE
// FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
// OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using ICSharpCode.Decompiler.FlowAnalysis;
using ICSharpCode.Decompiler.IL.Transforms;
using ICSharpCode.Decompiler.Util;

namespace ICSharpCode.Decompiler.IL.ControlFlow
{
	/// <summary>
	/// Detects 'if' structure and other non-loop aspects of control flow.
	/// </summary>
	/// <remarks>
	/// Order dependency: should run after loop detection.
	/// Blocks should be basic blocks prior to this transform.
	/// After this transform, they will be extended basic blocks.
	/// </remarks>
	public class ConditionDetection : IBlockTransform
	{
		private enum Keyword
		{
			Break,
			Return,
			Continue,
			Other
		}

		private BlockTransformContext context;
		private ControlFlowNode cfgNode;
		private BlockContainer currentContainer;
		private Block continueBlock;

		/// <summary>
		/// Builds structured control flow for the block associated with the control flow node.
		/// </summary>
		/// <remarks>
		/// After a block was processed, it should use structured control flow
		/// and have just a single 'regular' exit point (last branch instruction in the block)
		/// </remarks>
		public void Run(Block block, BlockTransformContext context)
		{
			this.context = context;
			currentContainer = (BlockContainer)block.Parent;

			// for detection of continue statements
			continueBlock = GuessContinueBlock();

			// We only embed blocks into this block if they aren't referenced anywhere else,
			// so those blocks are dominated by this block.
			// BlockILTransform thus guarantees that the blocks being embedded are already
			// fully processed.

			cfgNode = context.ControlFlowNode;
			Debug.Assert(cfgNode.UserData == block);

			// Because this transform runs at the beginning of the block transforms,
			// we know that `block` is still a (non-extended) basic block.
			
			// Previous-to-last instruction might have conditional control flow,
			// usually an IfInstruction with a branch:
			if (block.Instructions.SecondToLastOrDefault() is IfInstruction ifInst)
				HandleIfInstruction(block, ifInst);
			else
				InlineExitBranch(block);
		}

		/// <summary>
		/// Repeatedly inlines and simplifies, maintaining a good block exit and then attempting to match IL order
		/// </summary>
		private void HandleIfInstruction(Block block, IfInstruction ifInst)
		{
			while (InlineTrueBranch(ifInst) || InlineExitBranch(block)) {
				PickBetterBlockExit(block, ifInst);
				MergeCommonBranches(block, ifInst);
				SwapEmptyThen(ifInst);
				IntroduceShortCircuit(ifInst);
			}
			PickBetterBlockExit(block, ifInst);
			OrderIfBlocks(ifInst);
		}

		/// <summary>
		///   if (...) br trueBlock;
		/// ->
		///   if (...) { trueBlock... }
		/// 
		/// Only inlines branches that are strictly dominated by this block (incoming edge count == 1)
		/// </summary>
		private bool InlineTrueBranch(IfInstruction ifInst)
		{
			if (!CanInline(ifInst.TrueInst))
				return false;

			context.Step("Inline block as then-branch", ifInst.TrueInst);
			// The targetBlock was already processed, and is ready to embed
			var targetBlock = ((Branch)ifInst.TrueInst).TargetBlock;
			targetBlock.Remove();
			ifInst.TrueInst = targetBlock;

			return true;
		}
		
		/// <summary>
		///   ...; br nextBlock;
		/// ->
		///   ...; { nextBlock... }
		/// 
		/// Only inlines branches that are strictly dominated by this block (incoming edge count == 1)
		/// </summary>
		private bool InlineExitBranch(Block block)
		{
			var exitInst = GetExit(block);
			if (!CanInline(exitInst))
				return false;

			context.Step("Inline target block of unconditional branch", exitInst);
			// The targetBlock was already processed, and is ready to embed
			var targetBlock = ((Branch)exitInst).TargetBlock;
			block.Instructions.RemoveAt(block.Instructions.Count - 1);
			block.Instructions.AddRange(targetBlock.Instructions);
			targetBlock.Remove();

			return true;
		}

		/// <summary>
		/// Gets whether <c>potentialBranchInstruction</c> is a branch to a block that is dominated by <c>cfgNode</c>.
		/// If this function returns true, we replace the branch instruction with the block itself.
		/// </summary>
		private bool CanInline(ILInstruction exitInst)
		{
			if (exitInst is Branch branch
			    && branch.TargetBlock.Parent == currentContainer
			    && branch.TargetBlock.IncomingEdgeCount == 1
			    && branch.TargetBlock.FinalInstruction is Nop) {
				// if the incoming edge count is 1, then this must be the sole branch, and dominance is already ensured
				Debug.Assert(cfgNode.Dominates(context.ControlFlowGraph.GetNode(branch.TargetBlock)));
				return true;
			}

			return false;
		}

		/// <summary>
		/// Looks for common exits in the inlined then and else branches of an if instruction
		/// and performs inversions and simplifications to merge them provided they don't 
		/// isolate a higher priority block exit
		/// </summary>
		private void MergeCommonBranches(Block block, IfInstruction ifInst)
		{
			var thenExits = new List<ILInstruction>();
			AddExits(ifInst.TrueInst, 0, thenExits);
			if (thenExits.Count == 0)
				return;

			// if there are any exits from the then branch, then the else is redundant and shouldn't exist
			Debug.Assert(IsEmpty(ifInst.FalseInst));
			var elseExits = new List<ILInstruction>();
			int falseInstIndex = block.Instructions.IndexOf(ifInst) + 1;
			AddExits(block, falseInstIndex, elseExits);
			
			var commonExits = elseExits.Where(e1 => thenExits.Any(e2 => DetectExitPoints.CompatibleExitInstruction(e1, e2)));

			// find the common exit with the highest block exit priority
			ILInstruction commonExit = null;
			foreach (var exit in commonExits)
				if (commonExit == null || CompareBlockExitPriority(exit, commonExit) > 0)
					commonExit = exit;
			
			if (commonExit == null)
				return;

			// if the current block exit has higher priority than the exits to merge,
			// determine if this merge will isolate the current block exit
			// that is, no sequence of inversions can restore it to the block exit position
			var blockExit = block.Instructions.Last();
			if (CompareBlockExitPriority(blockExit, commonExit, true) > 0 && !WillShortCircuit(block, ifInst, commonExit))
				return;
			
			// could improve performance by directly implementing the || short-circuit when WillShortCircuit
			// currently the same general sequence of transformations introduces both operators

			context.StepStartGroup("Merge common branches "+commonExit, ifInst);
			ProduceExit(ifInst.TrueInst, 0, commonExit);
			ProduceExit(block, falseInstIndex, commonExit);
			
			// if (...) { ...; blockExit; } ...; blockExit;
			// -> if (...) { ...; blockExit; } else { ... } blockExit;
			if (ifInst != block.Instructions.SecondToLastOrDefault()) {
				context.Step("Embed else-block for goto removal", ifInst);
				Debug.Assert(IsEmpty(ifInst.FalseInst));
				ifInst.FalseInst = ExtractBlock(block, block.Instructions.IndexOf(ifInst) + 1, block.Instructions.Count - 2);
			}
			
			// if (...) { ...; goto blockExit; } blockExit;
			// -> if (...) { ... } blockExit;
			// OR
			// if (...) { ...; goto blockExit; } else { ... } blockExit;
			// -> if (...) { ... } else { ... } blockExit;
			context.Step("Remove redundant 'goto blockExit;' in then-branch", ifInst);
			if (!(ifInst.TrueInst is Block trueBlock) || trueBlock.Instructions.Count == 1)
				ifInst.TrueInst = new Nop { ILRange = ifInst.TrueInst.ILRange };
			else
				trueBlock.Instructions.RemoveAt(trueBlock.Instructions.Count - 1);

			context.StepEndGroup();
		}
		
		/// <summary>
		/// Finds all exits which could be brought to the block root via inversion
		/// </summary>
		private void AddExits(ILInstruction searchInst, int startIndex, IList<ILInstruction> exits)
		{
			if (!TryGetExit(searchInst, out var exitInst))
				return;

			exits.Add(exitInst);
			if (searchInst is Block block)
				for (int i = startIndex; i < block.Instructions.Count; i++)
					if (block.Instructions[i] is IfInstruction ifInst)
						AddExits(ifInst.TrueInst, 0, exits);
		}

		/// <summary>
		/// Recursively performs inversions to bring a desired exit to the root of the block
		/// for example:
		///   if (a) {
		///     ...;
		///     if (b) {
		///       ...;
		///       targetExit;
		///     }
		///     ...;
		///     exit1;
		///   }
		///   ...;
		///   exit2;
		/// ->
		///   if (!a) {
		///     ...;
		///     exit2;
		///   }
		///   ...;
		///   if (!b) {
		///     ...;
		///     exit1;
		///   }
		///   ...;
		///   targetExit;
		/// </summary>
		private bool ProduceExit(ILInstruction searchInst, int startIndex, ILInstruction targetExit)
		{
			if (!TryGetExit(searchInst, out var exitInst))
				return false;

			if (DetectExitPoints.CompatibleExitInstruction(exitInst, targetExit))
				return true;

			if (searchInst is Block block)
				for (int i = startIndex; i < block.Instructions.Count; i++)
					if (block.Instructions[i] is IfInstruction ifInst && ProduceExit(ifInst.TrueInst, 0, targetExit)) {
						InvertIf(block, ifInst);
						Debug.Assert(DetectExitPoints.CompatibleExitInstruction(GetExit(block), targetExit));
						return true;
					}
			
			return false;
		}

		/// <summary>
		/// Anticipates the introduction of an || operator when merging ifInst and elseExit
		/// 
		///   if (cond) commonExit;
		///   if (cond2) commonExit;
		///   ...;
		///   blockExit;
		/// will become:
		///   if (cond || cond2) commonExit;
		///   ...;
		///   blockExit;
		/// </summary>
		private bool WillShortCircuit(Block block, IfInstruction ifInst, ILInstruction elseExit)
		{
			bool ThenInstIsSingleExit(ILInstruction inst) =>
				inst.MatchIfInstruction(out var _, out var trueInst)
				&& (!(trueInst is Block trueBlock) || trueBlock.Instructions.Count == 1)
				&& TryGetExit(trueInst, out var _);

			if (!ThenInstIsSingleExit(ifInst))
				return false;

			// find the host if statement
			var elseIfInst = elseExit;
			while (elseIfInst.Parent != block)
				elseIfInst = elseIfInst.Parent;
			
			return block.Instructions.IndexOf(elseIfInst) == block.Instructions.IndexOf(ifInst) + 1 
			       && ThenInstIsSingleExit(elseIfInst);
		}

		/// <summary>
		///   if (cond) { then... }
		///   else...;
		/// ->
		///   if (!cond) { else... }
		///   then...;
		/// 
		/// Assumes ifInst does not have an else block
		/// </summary>
		private void InvertIf(Block block, IfInstruction ifInst)
		{
			Debug.Assert(ifInst.Parent == block);
			
			//assert then block terminates
			var trueExitInst = GetExit(ifInst.TrueInst);
			var exitInst = GetExit(block);
			context.Step("Negate if for desired branch "+trueExitInst, ifInst);
			
			//if the then block terminates, else blocks are redundant, and should not exist
			Debug.Assert(IsEmpty(ifInst.FalseInst));

			//save a copy
			var trueInst = ifInst.TrueInst;
			
			if (ifInst != block.Instructions.SecondToLastOrDefault()) {
				ifInst.TrueInst = ExtractBlock(block, block.Instructions.IndexOf(ifInst) + 1, block.Instructions.Count - 1);
			} else {
				block.Instructions.RemoveAt(block.Instructions.Count - 1);
				ifInst.TrueInst = exitInst;
			}

			if (trueInst is Block trueBlock) {
				block.Instructions.AddRange(trueBlock.Instructions);
			} else
				block.Instructions.Add(trueInst);

			ifInst.Condition = Comp.LogicNot(ifInst.Condition);
			ExpressionTransforms.RunOnSingleStatment(ifInst, context);
		}

		/// <summary>
		///   if (cond) { } else { ... }
		/// ->
		///   if (!cond) { ... }
		/// </summary>
		private void SwapEmptyThen(IfInstruction ifInst)
		{
			if (!IsEmpty(ifInst.TrueInst))
				return;

			context.Step("Swap empty then-branch with else-branch", ifInst);
			var oldTrue = ifInst.TrueInst;
			ifInst.TrueInst = ifInst.FalseInst;
			ifInst.FalseInst = new Nop { ILRange = oldTrue.ILRange };
			ifInst.Condition = Comp.LogicNot(ifInst.Condition);
		}

		/// <summary>
		///   if (cond) { if (nestedCond) { nestedThen... } }
		/// ->
		///   if (cond && nestedCond) { nestedThen... }
		/// </summary>
		private void IntroduceShortCircuit(IfInstruction ifInst)
		{
			if (IsEmpty(ifInst.FalseInst) 
					&& ifInst.TrueInst is Block trueBlock 
					&& trueBlock.Instructions.Count == 1
					&& trueBlock.FinalInstruction is Nop
					&& trueBlock.Instructions[0].MatchIfInstruction(out var nestedCondition, out var nestedTrueInst)) {
				context.Step("Combine 'if (cond1 && cond2)' in then-branch", ifInst);
				ifInst.Condition = IfInstruction.LogicAnd(ifInst.Condition, nestedCondition);
				ifInst.TrueInst = nestedTrueInst;
			}
		}

		/// <summary>
		///   if (cond) { lateBlock... } else { earlyBlock... }
		/// ->
		///   if (!cond) { earlyBlock... } else { lateBlock... }
		/// </summary>
		private void OrderIfBlocks(IfInstruction ifInst)
		{
			if (IsEmpty(ifInst.FalseInst) || ifInst.TrueInst.ILRange.Start <= ifInst.FalseInst.ILRange.Start)
				return;

			context.Step("Swap then-branch with else-branch to match IL order", ifInst);
			var oldTrue = ifInst.TrueInst;
			ifInst.TrueInst = ifInst.FalseInst;
			ifInst.FalseInst = oldTrue;
			ifInst.Condition = Comp.LogicNot(ifInst.Condition);
		}

		/// <summary>
		/// Compares the current block exit, and the exit of ifInst.ThenInst 
		/// and inverts if necessary to pick the better exit
		/// 
		/// Does nothing when ifInst has an else block (because inverting wouldn't affect the block exit)
		/// </summary>
		private void PickBetterBlockExit(Block block, IfInstruction ifInst)
		{
			var exitInst = GetExit(block);
			if (IsEmpty(ifInst.FalseInst) 
			      && TryGetExit(ifInst.TrueInst, out var trueExitInst)
			      && CompareBlockExitPriority(trueExitInst, exitInst) > 0)
				InvertIf(block, ifInst);
		}
		
		/// <summary>
		/// Compares two exit instructions for block exit priority
		/// A higher priority exit should be kept as the last instruction in a block
		/// even if it prevents the merging of a two compatible lower priority exits
		/// 
		/// leave from try containers must always be the final instruction, or a goto will be inserted
		/// loops will endeavour to leave at least one continue branch as the last instruction in the block
		/// 
		/// The priority is:
		///   leave > branch > other-keyword > continue > return > break
		/// 
		/// non-keyword leave instructions are ordered with the outer container having higher priority
		/// 
		/// if the exits have equal priority, and the <c>strongly</c> flag is not provided
		/// then the exits are sorted by IL order (target block for branches)
		/// 
		/// break has higher priority than other keywords in a switch block (for aesthetic reasons)
		/// </summary>
		/// <returns>{-1, 0, 1} if exit1 has {lower, equal, higher} priority an exit2</returns>
		private int CompareBlockExitPriority(ILInstruction exit1, ILInstruction exit2, bool strongly = false)
		{
			// keywords have lower priority than non-keywords
			bool isKeyword1 = IsKeywordExit(exit1, out var keyword1), isKeyword2 = IsKeywordExit(exit2, out var keyword2);
			if (isKeyword1 != isKeyword2)
				return isKeyword1 ? -1 : 1;

			
			if (isKeyword1) {
				//for keywords
				if (currentContainer.Kind == ContainerKind.Switch) {
					// breaks have highest priority in a switch
					if ((keyword1 == Keyword.Break) != (keyword2 == Keyword.Break))
						return keyword1 == Keyword.Break ? 1 : -1;

				} else {
					// breaks have lowest priority
					if ((keyword1 == Keyword.Break) != (keyword2 == Keyword.Break))
						return keyword1 == Keyword.Break ? -1 : 1;

					// continue has highest priority (to prevent having to jump to the end of a loop block)
					if ((keyword1 == Keyword.Continue) != (keyword2 == Keyword.Continue))
						return keyword1 == Keyword.Continue ? 1 : -1;
				}
			} else {// for non-keywords (only Branch or Leave)
				// branches have lower priority than non-keyword leaves
				bool isBranch1 = exit1 is Branch, isBranch2 = exit2 is Branch;
				if (isBranch1 != isBranch2)
					return isBranch1 ? -1 : 1;

				// two leaves that both want end of block priority
				if (exit1.MatchLeave(out var container1) && exit2.MatchLeave(out var container2) && container1 != container2) {
					// choose the outer one
					return container2.IsDescendantOf(container1) ? 1 : -1;
				}
			}

			if (strongly)
				return 0;

			if (exit1.MatchBranch(out var block1) && exit2.MatchBranch(out var block2)) {
				// prefer arranging stuff in IL order
				return block1.ILRange.Start.CompareTo(block2.ILRange.Start);
			}
			// prefer arranging stuff in IL order
			return exit1.ILRange.Start.CompareTo(exit2.ILRange.Start);
		}

		/// <summary>
		/// Determines if an exit instruction has a corresponding keyword and thus doesn't strictly need merging
		/// Branches can be 'continue' or goto (non-keyword)
		/// Leave can be 'return', 'break' or a pinned container exit (try/using/lock etc)
		/// All other instructions (throw, using, etc) are returned as Keyword.Other
		/// </summary>
		private bool IsKeywordExit(ILInstruction exitInst, out Keyword keyword)
		{
			keyword = Keyword.Other;
			switch (exitInst) {
				case Branch branch:
					if (branch.TargetBlock == continueBlock) {
						keyword = Keyword.Continue;
						return true;
					}
					return false;
				case Leave leave:
					if (leave.IsLeavingFunction) {
						keyword = Keyword.Return;
						return true;
					}
					if (leave.TargetContainer.Kind != ContainerKind.Normal) {
						keyword = Keyword.Break;
						return true;
					}
					return false;
				default:
					return true;
			}
		}

		/// <summary>
		/// Determine if the specified instruction necessarily exits (EndPointUnreachable)
		/// and if so return last (or single) exit instruction
		/// </summary>
		private static bool TryGetExit(ILInstruction inst, out ILInstruction exitInst)
		{
			if (inst is Block block && block.Instructions.Count > 0)
				inst = block.Instructions.Last();

			if (inst.HasFlag(InstructionFlags.EndPointUnreachable)) {
				exitInst = inst;
				return true;
			}
			
			exitInst = null;
			return false;
		}

		/// <summary>
		/// Gets the final instruction from a block (or a single instruction) assuming that all blocks
		/// or instructions in this position have unreachable endpoints
		/// </summary>
		private static ILInstruction GetExit(ILInstruction inst)
		{
			ILInstruction exitInst = inst is Block block ? block.Instructions.Last() : inst;
			// Last instruction is one with unreachable endpoint
			// (guaranteed by combination of BlockContainer and Block invariants)
			Debug.Assert(exitInst.HasFlag(InstructionFlags.EndPointUnreachable));
			return exitInst;
		}

		/// <summary>
		/// Returns true if inst is Nop or a Block with no instructions.
		/// </summary>
		private static bool IsEmpty(ILInstruction inst) => 
			inst is Nop || inst is Block block && block.Instructions.Count == 0 && block.FinalInstruction is Nop;

		/// <summary>
		/// Import some pattern matching from HighLevelLoopTransform to guess the continue block of loop containers.
		/// Used to identify branches targetting this block as continue statements, for ordering priority.
		/// </summary>
		/// <returns></returns>
		private Block GuessContinueBlock()
		{
			if (currentContainer.Kind != ContainerKind.Loop)
				return null;

			// continue blocks have exactly 2 incoming edges
			if (currentContainer.EntryPoint.IncomingEdgeCount == 2) {
				try {
					var forIncrement = HighLevelLoopTransform.GetIncrementBlock(currentContainer, currentContainer.EntryPoint);
					if (forIncrement != null)
						return forIncrement;
				} catch (InvalidOperationException) {
					// multiple potential increment blocks. Can get this because we don't check that the while loop
					// has a condition first, as we don't need to do too much of HighLevelLoopTransform's job.
				}
			}

			return currentContainer.EntryPoint;
		}

		/// <summary>
		/// Removes a subrange of instructions from a block and returns them in a new Block
		/// </summary>
		internal static Block ExtractBlock(Block block, int firstInstIndex, int lastInstIndex)
		{
			var extractedBlock = new Block();
			for (int i = firstInstIndex; i <=  lastInstIndex; i++) {
				var inst = block.Instructions[firstInstIndex];
				block.Instructions.RemoveAt(firstInstIndex);

				extractedBlock.Instructions.Add(inst);
				extractedBlock.AddILRange(inst.ILRange);
			}

			return extractedBlock;
		}
	}
}
