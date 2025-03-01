// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Nethermind.Consensus;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Specs;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.State;
using Nethermind.TxPool.Comparison;

namespace Nethermind.AccountAbstraction.Executor
{
    public class AABlockProducerTransactionsExecutor : BlockProcessor.BlockProductionTransactionsExecutor
    {
        private readonly IStateProvider _stateProvider;
        private readonly IStorageProvider _storageProvider;
        private readonly ISigner _signer;
        private readonly Address[] _entryPointAddresses;

        public AABlockProducerTransactionsExecutor(
            ITransactionProcessor transactionProcessor,
            IStateProvider stateProvider,
            IStorageProvider storageProvider,
            ISpecProvider specProvider,
            ILogManager logManager,
            ISigner signer,
            Address[] entryPointAddresses)
            : base(
            transactionProcessor,
            stateProvider,
            storageProvider,
            specProvider,
            logManager)
        {
            _stateProvider = stateProvider;
            _storageProvider = storageProvider;
            _signer = signer;
            _entryPointAddresses = entryPointAddresses;
        }

        public override TxReceipt[] ProcessTransactions(
            Block block,
            ProcessingOptions processingOptions,
            BlockReceiptsTracer receiptsTracer,
            IReleaseSpec spec)
        {
            IEnumerable<Transaction> transactions = GetTransactions(block);

            int i = 0;
            LinkedHashSet<Transaction> transactionsInBlock = new(ByHashTxComparer.Instance);
            foreach (Transaction transaction in transactions)
            {
                if (IsAccountAbstractionTransaction(transaction))
                {
                    BlockProcessor.TxAction action = ProcessAccountAbstractionTransaction(block, transaction, i++, receiptsTracer, processingOptions, transactionsInBlock);
                    if (action == BlockProcessor.TxAction.Stop) break;
                }
                else
                {
                    BlockProcessor.TxAction action = ProcessTransaction(block, transaction, i++, receiptsTracer, processingOptions, transactionsInBlock);
                    if (action == BlockProcessor.TxAction.Stop) break;
                }
            }

            _stateProvider.Commit(spec, receiptsTracer);
            _storageProvider.Commit(receiptsTracer);

            SetTransactions(block, transactionsInBlock);
            return receiptsTracer.TxReceipts.ToArray();
        }

        private bool IsAccountAbstractionTransaction(Transaction transaction)
        {
            if (transaction.SenderAddress != _signer.Address) return false;
            if (!_entryPointAddresses.Contains(transaction.To)) return false;
            return true;
        }

        private BlockProcessor.TxAction ProcessAccountAbstractionTransaction(
            Block block,
            Transaction currentTx,
            int index,
            BlockReceiptsTracer receiptsTracer,
            ProcessingOptions processingOptions,
            LinkedHashSet<Transaction> transactionsInBlock)
        {
            int snapshot = receiptsTracer.TakeSnapshot();

            BlockProcessor.TxAction action = ProcessTransaction(block, currentTx, index, receiptsTracer, processingOptions, transactionsInBlock, false);
            if (action != BlockProcessor.TxAction.Add)
            {
                return action;
            }

            string? error = receiptsTracer.LastReceipt.Error;
            bool transactionSucceeded = string.IsNullOrEmpty(error);
            if (!transactionSucceeded)
            {
                receiptsTracer.Restore(snapshot);
                return BlockProcessor.TxAction.Skip;
            }

            transactionsInBlock.Add(currentTx);
            _transactionProcessed?.Invoke(this, new TxProcessedEventArgs(index, currentTx, receiptsTracer.TxReceipts[index]));
            return BlockProcessor.TxAction.Add;
        }

    }
}
