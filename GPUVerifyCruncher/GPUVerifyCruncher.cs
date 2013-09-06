//===-----------------------------------------------------------------------==//
//
//                GPUVerify - a Verifier for GPU Kernels
//
// This file is distributed under the Microsoft Public License.  See
// LICENSE.TXT for details.
//
//===----------------------------------------------------------------------===//


﻿using GPUVerify;

namespace Microsoft.Boogie
{
  using System;
  using System.IO;
  //using System.Collections;
  using System.Collections.Generic;
  using System.Diagnostics;
  using System.Diagnostics.Contracts;
  using System.Linq;
  using VC;
  //using Microsoft.Boogie;
  //using Microsoft.Boogie.AbstractInterpretation;

  public class GPUVerifyCruncher
  {
    public static void Main(string[] args)
    {
      Contract.Requires(cce.NonNullElements(args));
      CommandLineOptions.Install(new GPUVerifyKernelAnalyserCommandLineOptions());

      try {
        CommandLineOptions.Clo.RunningBoogieFromCommandLine = true;

        if (!CommandLineOptions.Clo.Parse(args)) {
          Environment.Exit(1);
        }
        if (CommandLineOptions.Clo.Files.Count == 0) {
          GVUtil.IO.ErrorWriteLine("GPUVerify: error: no input files were specified");
          Environment.Exit(1);
        }
        if (!CommandLineOptions.Clo.DontShowLogo) {
          Console.WriteLine(CommandLineOptions.Clo.Version);
        }

        List<string> fileList = new List<string>();

        foreach (string file in CommandLineOptions.Clo.Files) {
          string extension = Path.GetExtension(file);
          if (extension != null) {
            extension = extension.ToLower();
          }
          fileList.Add(file);
        }
        foreach (string file in fileList) {
          Contract.Assert(file != null);
          string extension = Path.GetExtension(file);
          if (extension != null) {
            extension = extension.ToLower();
          }
          if (extension != ".bpl") {
            GVUtil.IO.ErrorWriteLine("GPUVerify: error: {0} is not a .bpl file", file);
            Environment.Exit(1);
          }
        }

        int exitCode = InferInvariantsInFiles(fileList);
        Environment.Exit(exitCode);
      } catch (Exception e) {
        if(GetCommandLineOptions().DebugGPUVerify) {
          Console.Error.WriteLine("Exception thrown in GPUVerifyCruncher");
          Console.Error.WriteLine(e);
          throw e;
        }

        const string DUMP_FILE = "__gvdump.txt";

        #region Give generic internal error messsage
        Console.Error.WriteLine("\nGPUVerify: an internal error has occurred, details written to " + DUMP_FILE + ".");
        Console.Error.WriteLine();
        Console.Error.WriteLine("Please consult the troubleshooting guide in the GPUVerify documentation");
        Console.Error.WriteLine("for common problems, and if this does not help, raise an issue via the");
        Console.Error.WriteLine("GPUVerify issue tracker:");
        Console.Error.WriteLine();
        Console.Error.WriteLine("  https://gpuverify.codeplex.com");
        Console.Error.WriteLine();
        #endregion"

        #region Now try to give the user a specific hint if this looks like a common problem
        try {
          throw e;
        } catch(ProverException) {
          Console.Error.WriteLine("Hint: It looks like GPUVerify is having trouble invoking its");
          Console.Error.WriteLine("supporting theorem prover, which by default is Z3.  Have you");
          Console.Error.WriteLine("installed Z3?");
        } catch(Exception) {
          // Nothing to say about this
        }
        #endregion

        #region Write details of the exception to the dump file
        using (TokenTextWriter writer = new TokenTextWriter(DUMP_FILE)) {
          writer.Write("Exception ToString:");
          writer.Write("===================");
          writer.Write(e.ToString());
          writer.Close();
        }
        #endregion

        Environment.Exit(1);
      }
    }

    static int InferInvariantsInFiles(List<string> fileNames)
    {
      Contract.Requires(cce.NonNullElements(fileNames));

      var dir = Path.GetDirectoryName(fileNames[fileNames.Count - 1]) + Path.VolumeSeparatorChar;
      var file = Path.GetFileNameWithoutExtension(fileNames[fileNames.Count - 1]);

      Houdini.Houdini houdini = null;

      #region Compute invariant without race checking
      {
        if (CommandLineOptions.Clo.Trace) {
          Console.WriteLine("Compute invariant without race checking");
        }

        Program InvariantComputationProgram = GVUtil.IO.ParseBoogieProgram(fileNames, false);
        if (InvariantComputationProgram == null) return 1;

        PipelineOutcome oc = ResolveAndTypecheck(InvariantComputationProgram, fileNames[fileNames.Count - 1]);
        if (oc != PipelineOutcome.ResolvedAndTypeChecked) return 1;

        DisableRaceChecking(InvariantComputationProgram);
        EliminateDeadVariablesAndInline(InvariantComputationProgram);
        CheckForQuantifiersAndSpecifyLogic(InvariantComputationProgram);

        var houdiniStats = new Houdini.HoudiniSession.HoudiniStatistics();
        houdini = new Houdini.Houdini(InvariantComputationProgram, houdiniStats);
        Houdini.HoudiniOutcome outcome = houdini.PerformHoudiniInference();

        if (CommandLineOptions.Clo.PrintAssignment) {
          Console.WriteLine("Assignment computed by Houdini:");
          foreach (var x in outcome.assignment) {
            Console.WriteLine(x.Key + " = " + x.Value);
          }
        }

        if (CommandLineOptions.Clo.Trace) {
          int numTrueAssigns = 0;
          foreach (var x in outcome.assignment) {
            if (x.Value)
              numTrueAssigns++;
          }

          Console.WriteLine("Number of true assignments = " + numTrueAssigns);
          Console.WriteLine("Number of false assignments = " + (outcome.assignment.Count - numTrueAssigns));
          Console.WriteLine("Prover time = " + houdiniStats.proverTime.ToString("F2"));
          Console.WriteLine("Unsat core prover time = " + houdiniStats.unsatCoreProverTime.ToString("F2"));
          Console.WriteLine("Number of prover queries = " + houdiniStats.numProverQueries);
          Console.WriteLine("Number of unsat core prover queries = " + houdiniStats.numUnsatCoreProverQueries);
          Console.WriteLine("Number of unsat core prunings = " + houdiniStats.numUnsatCorePrunings);
        }

        if (!AllImplementationsValid(outcome)) {
          int verified = 0;
          int errorCount = 0;
          int inconclusives = 0;
          int timeOuts = 0;
          int outOfMemories = 0;

          foreach (Houdini.VCGenOutcome x in outcome.implementationOutcomes.Values) {
            ProcessOutcome(x.outcome, x.errors, "", ref errorCount, ref verified, ref inconclusives, ref timeOuts, ref outOfMemories);
          }

          GVUtil.IO.WriteTrailer(verified, errorCount, inconclusives, timeOuts, outOfMemories);
          return errorCount + inconclusives + timeOuts + outOfMemories;
        }
      }
      #endregion

      #region Use computed invariant (if any) to perform race checking
      {

        Program RaceCheckingProgram = GVUtil.IO.ParseBoogieProgram(fileNames, false);
        if (RaceCheckingProgram == null) return 1;

        PipelineOutcome oc = ResolveAndTypecheck(RaceCheckingProgram, fileNames[fileNames.Count - 1]);
        if (oc != PipelineOutcome.ResolvedAndTypeChecked) return 1;

        EliminateDeadVariablesAndInline(RaceCheckingProgram);

        CommandLineOptions.Clo.PrintUnstructured = 2;

        if (CommandLineOptions.Clo.LoopUnrollCount != -1) {
          Debug.Assert(!CommandLineOptions.Clo.ContractInfer);
          RaceCheckingProgram.UnrollLoops(CommandLineOptions.Clo.LoopUnrollCount, CommandLineOptions.Clo.SoundLoopUnrolling);
          GPUVerifyErrorReporter.FixStateIds(RaceCheckingProgram);
        }

        if(houdini != null) {
          houdini.ApplyAssignment(RaceCheckingProgram);
        }

        //PrintBplFile(Path.GetFullPath(fileNames[fileNames.Count - 1]), RaceCheckingProgram, true);
        File.Delete(dir + file + ".bpl");
        //File.Move(dir + file + ".bpl", dir + "old.bpl");
        GPUVerify.GVUtil.IO.emitProgram(RaceCheckingProgram, dir + file);
      }
      #endregion

      return 0;
    }

    enum PipelineOutcome
    {
      Done,
      ResolutionError,
      TypeCheckingError,
      ResolvedAndTypeChecked,
      FatalError,
      VerificationCompleted
    }

    /// <summary>
    /// Resolves and type checks the given Boogie program.  Any errors are reported to the
    /// console.  Returns:
    ///  - Done if no errors occurred, and command line specified no resolution or no type checking.
    ///  - ResolutionError if a resolution error occurred
    ///  - TypeCheckingError if a type checking error occurred
    ///  - ResolvedAndTypeChecked if both resolution and type checking succeeded
    /// </summary>
    static PipelineOutcome ResolveAndTypecheck(Program program, string bplFileName)
    {
      Contract.Requires(program != null);
      Contract.Requires(bplFileName != null);

      // ---------- Resolve ----------

      if (CommandLineOptions.Clo.NoResolve) {
        return PipelineOutcome.Done;
      }

      int errorCount = program.Resolve();
      if (errorCount != 0) {
        Console.WriteLine("{0} name resolution errors detected in {1}", errorCount, bplFileName);
        return PipelineOutcome.ResolutionError;
      }

      // ---------- Type check ----------

      if (CommandLineOptions.Clo.NoTypecheck) {
        return PipelineOutcome.Done;
      }

      errorCount = program.Typecheck();
      if (errorCount != 0) {
        Console.WriteLine("{0} type checking errors detected in {1}", errorCount, bplFileName);
        return PipelineOutcome.TypeCheckingError;
      }

      LinearTypechecker linearTypechecker = new LinearTypechecker(program);
      linearTypechecker.VisitProgram(program);
      if (linearTypechecker.errorCount > 0) {
        Console.WriteLine("{0} type checking errors detected in {1}", errorCount, bplFileName);
        return PipelineOutcome.TypeCheckingError;
      }

      if (CommandLineOptions.Clo.PrintFile != null && CommandLineOptions.Clo.PrintDesugarings) {
        // if PrintDesugaring option is engaged, print the file here, after resolution and type checking
        GVUtil.IO.PrintBplFile(CommandLineOptions.Clo.PrintFile, program, true);
      }

      return PipelineOutcome.ResolvedAndTypeChecked;
    }

    private static void DisableRaceChecking(Program program)
    {
      foreach (var block in program.Blocks()) {
        List<Cmd> newCmds = new List<Cmd>();
        foreach (Cmd c in block.Cmds) {
          CallCmd callCmd = c as CallCmd;
          // TODO: refine into proper check
          if(callCmd == null || !(callCmd.callee.Contains("_CHECK_READ") || 
                                  callCmd.callee.Contains("_CHECK_WRITE"))) {
            newCmds.Add(c);
          }
        }
        block.Cmds = newCmds;
      }
    }

    static void EliminateDeadVariablesAndInline(Program program)
    {
      Contract.Requires(program != null);

      // Eliminate dead variables
      Microsoft.Boogie.UnusedVarEliminator.Eliminate(program);

      // Collect mod sets
      if (CommandLineOptions.Clo.DoModSetAnalysis) {
        Microsoft.Boogie.ModSetCollector.DoModSetAnalysis(program);
      }

      // Coalesce blocks
      if (CommandLineOptions.Clo.CoalesceBlocks) {
        if (CommandLineOptions.Clo.Trace)
          Console.WriteLine("Coalescing blocks...");
        Microsoft.Boogie.BlockCoalescer.CoalesceBlocks(program);
      }

      // Inline
      var TopLevelDeclarations = cce.NonNull(program.TopLevelDeclarations);

      if (CommandLineOptions.Clo.ProcedureInlining != CommandLineOptions.Inlining.None) {
        bool inline = false;
        foreach (var d in TopLevelDeclarations) {
          if (d.FindExprAttribute("inline") != null) {
            inline = true;
          }
        }
        if (inline) {
          foreach (var d in TopLevelDeclarations) {
            var impl = d as Implementation;
            if (impl != null) {
              impl.OriginalBlocks = impl.Blocks;
              impl.OriginalLocVars = impl.LocVars;
            }
          }
          foreach (var d in TopLevelDeclarations) {
            var impl = d as Implementation;
            if (impl != null && !impl.SkipVerification) {
              Inliner.ProcessImplementation(program, impl);
            }
          }
          foreach (var d in TopLevelDeclarations) {
            var impl = d as Implementation;
            if (impl != null) {
              impl.OriginalBlocks = null;
              impl.OriginalLocVars = null;
            }
          }
        }
      }
    }

    /// <summary>
    /// Checks if Quantifiers exists in the Boogie program. If they exist and the underlying
    /// parser is CVC4 then it enables the corresponding Logic.
    /// </summary>
    static void CheckForQuantifiersAndSpecifyLogic(Program program)
    {
      if ((CommandLineOptions.Clo.ProverOptions.Contains("SOLVER=cvc4") ||
           CommandLineOptions.Clo.ProverOptions.Contains("SOLVER=CVC4")) &&
          CommandLineOptions.Clo.ProverOptions.Contains("LOGIC=QF_ALL_SUPPORTED") &&
          CheckForQuantifiers.Found(program)) {
        CommandLineOptions.Clo.ProverOptions.Remove("LOGIC=QF_ALL_SUPPORTED");
        CommandLineOptions.Clo.ProverOptions.Add("LOGIC=ALL_SUPPORTED");
      }
    }

    static void ProcessOutcome(VC.VCGen.Outcome outcome, List<Counterexample> errors, string timeIndication,
                               ref int errorCount, ref int verified, ref int inconclusives, ref int timeOuts, ref int outOfMemories)
    {
      switch (outcome) {
        default:
          Contract.Assert(false);  // unexpected outcome
          throw new cce.UnreachableException();
        case VCGen.Outcome.ReachedBound:
          GVUtil.IO.Inform(String.Format("{0}verified", timeIndication));
          Console.WriteLine(string.Format("Stratified Inlining: Reached recursion bound of {0}", CommandLineOptions.Clo.RecursionBound));
          verified++;
          break;
        case VCGen.Outcome.Correct:
          if (CommandLineOptions.Clo.vcVariety == CommandLineOptions.VCVariety.Doomed) {
            GVUtil.IO.Inform(String.Format("{0}credible", timeIndication));
            verified++;
          }
          else {
            GVUtil.IO.Inform(String.Format("{0}verified", timeIndication));
            verified++;
          }
          break;
        case VCGen.Outcome.TimedOut:
          timeOuts++;
          GVUtil.IO.Inform(String.Format("{0}timed out", timeIndication));
          break;
        case VCGen.Outcome.OutOfMemory:
          outOfMemories++;
          GVUtil.IO.Inform(String.Format("{0}out of memory", timeIndication));
          break;
        case VCGen.Outcome.Inconclusive:
          inconclusives++;
          GVUtil.IO.Inform(String.Format("{0}inconclusive", timeIndication));
          break;
        case VCGen.Outcome.Errors:
          if (CommandLineOptions.Clo.vcVariety == CommandLineOptions.VCVariety.Doomed) {
            GVUtil.IO.Inform(String.Format("{0}doomed", timeIndication));
            errorCount++;
          } //else {
          Contract.Assert(errors != null);  // guaranteed by postcondition of VerifyImplementation
          {
            // BP1xxx: Parsing errors
            // BP2xxx: Name resolution errors
            // BP3xxx: Typechecking errors
            // BP4xxx: Abstract interpretation errors (Is there such a thing?)
            // BP5xxx: Verification errors

            errors.Sort(new CounterexampleComparer());
            foreach (Counterexample error in errors)
            {
              GPUVerifyErrorReporter.ReportCounterexample(error);
              errorCount++;
            }
            //}
            GVUtil.IO.Inform(String.Format("{0}error{1}", timeIndication, errors.Count == 1 ? "" : "s"));
          }
          break;
      }
    }

    private static bool AllImplementationsValid(Houdini.HoudiniOutcome outcome)
    {
      foreach (var vcgenOutcome in outcome.implementationOutcomes.Values.Select(i => i.outcome)) {
        if (vcgenOutcome != VCGen.Outcome.Correct) {
          return false;
        }
      }
      return true;
    }

    private static GPUVerifyKernelAnalyserCommandLineOptions GetCommandLineOptions()
    {
      return (GPUVerifyKernelAnalyserCommandLineOptions)CommandLineOptions.Clo;
    }
  }
}
