//===-----------------------------------------------------------------------==//
//
//                GPUVerify - a Verifier for GPU Kernels
//
// This file is distributed under the Microsoft Public License.  See
// LICENSE.TXT for details.
//
//===----------------------------------------------------------------------===//

namespace GPUVerify
{
    using Microsoft.Boogie;

    internal class AccessRecord
    {
        public Variable v;
        public Expr Index;

        public AccessRecord(Variable v, Expr Index)
        {
            this.v = v;
            this.Index = Index;
        }

    }

}
