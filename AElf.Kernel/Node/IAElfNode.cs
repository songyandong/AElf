﻿using System.Collections.Generic;
using System.Threading.Tasks;
using AElf.Cryptography.ECDSA;
using AElf.Kernel.Miner;

namespace AElf.Kernel.Node
{
    public interface IAElfNode
    {
        bool Start(ECKeyPair nodeKeyPair, bool startRpc, string initData, byte[] code = null);

        List<Hash> GetMissingTransactions(IBlock block);
        Task<BlockExecutionResult> AddBlock(IBlock block);

        int GetCurrentChainHeight();
    }
}