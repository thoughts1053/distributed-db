using System;
using System.Collections.Generic;
using System.Linq;
using DistributedDb.Deadlocks;
using DistributedDb.Operations;
using DistributedDb.Sites;

namespace DistributedDb.Transactions
{
    public class TransactionManager
    {
        public TransactionManager(Clock clock, SiteManager manager)
        {
            Clock = clock;
            SiteManager = manager;
            Transactions = new List<Transaction>();
            DeadlockManager = new DeadlockManager(manager, clock);
        }

        public Clock Clock { get; set; }

        public IList<Transaction> Transactions { get; set; }

        public SiteManager SiteManager { get; set; }

        public DeadlockManager DeadlockManager { get; set; }

        public void Execute(IEnumerable<Operation> operations)
        {
            RerunTransactions();
            
            foreach (var operation in operations)
            {
                RunOperation(operation);
            }
        }

        private void RerunTransactions()
        {
            var transactions = Transactions.Where(t => t.IsWaiting())
                .OrderBy(t => t.WaitTime);

            foreach (var transaction in transactions)
            {
                RunOperation(transaction.OperationBuffer, transaction);
            }
        }

        public void RunOperation(Operation operation, Transaction transaction = null)
        {
            switch (operation.Type)
            {
                case OperationType.Begin:
                case OperationType.BeginRO:
                    BeginTransaction(operation);
                    break;
                case OperationType.Read:
                    ReadVariable(operation, transaction);
                    break;
                case OperationType.Write:
                    WriteVariable(operation, transaction);
                    break;
                case OperationType.End:
                    EndTransaction(operation);
                    break;
                default:
                    Logger.Fail($"Operation '{operation}' is not supported.");
                    break;
            }
        }

        public void BeginTransaction(Operation operation)
        {
            if (Transactions.Any(t => t.Name == operation.Transaction))
            {
                Logger.Fail($"Trying to begin transaction {operation.Transaction} when it already exists.");
            }

            var transaction = new Transaction
            {
                Name = operation.Transaction,
                IsReadOnly = operation.Type == OperationType.BeginRO,
                StartTime = Clock.Time
            };

            Transactions.Add(transaction);
        }

        public void ReadVariable(Operation operation, Transaction transaction = null)
        {
            transaction = transaction ?? GetTransaction(operation.Transaction);
            var variableName = operation.Variable;

            var stableSites = SiteManager.SitesWithVariable(variableName, SiteState.Stable);

            if (stableSites.Count() == 0)
            {
                BufferOperation(transaction, operation, TransactionState.Waiting);
                return;
            }

            foreach (var site in stableSites)
            {
                if (site.GetReadLock(transaction, variableName))
                {
                    var value = site.ReadData(transaction, variableName);
                    transaction.AddSite(site, Clock.Time);
                    Logger.Write($"{Clock.ToString()} Transaction {transaction.ToString()} reads {variableName}={value} from {site.ToString()}.");
                    transaction.ClearBuffer();
                    return;
                }
            }

            BufferOperation(transaction, operation, TransactionState.Blocked);
        }

        public void WriteVariable(Operation operation, Transaction transaction = null)
        {
            transaction = transaction ?? GetTransaction(operation.Transaction);
            var variableName = operation.Variable;
            var newValue = (int) operation.WriteValue;

            var lockedAllSites = true;
            var stableSites = SiteManager.SitesWithVariable(variableName, SiteState.Stable);

            if (stableSites.Count() == 0)
            {
                BufferOperation(transaction, operation, TransactionState.Waiting);
                return;
            }

            foreach (var site in stableSites)
            {
                if (!site.GetWriteLock(transaction, variableName))
                {
                    lockedAllSites = false;
                }
                else
                {
                    transaction.AddSite(site, Clock.Time);
                }
            }

            if (lockedAllSites)
            {
                transaction.ClearBuffer();
                foreach (var site in stableSites)
                {
                    site.WriteData(variableName, newValue);
                }
            }
            else
            {
                BufferOperation(transaction, operation, TransactionState.Blocked);
            }
        }

        public void EndTransaction(Operation operation)
        {
            var transaction = GetTransaction(operation.Transaction);

            transaction.EndTime = Clock.Time;
            if (transaction.CanCommit())
            {
                Commit(transaction);
            }
            else
            {
                Abort(transaction);
            }

            RerunTransactions();
        }

        public void Commit(Transaction transaction)
        {
            Logger.Write($"{Clock.ToString()} Transaction {transaction.ToString()} committed.");
            transaction.State = TransactionState.Committed;
            foreach (var site in transaction.GetStableSites())
            {
                site.CommitValue(transaction);
                site.ClearLocks(transaction);
            }
        }

        public void Abort(Transaction transaction)
        {
            Logger.Write($"{Clock.ToString()} Transaction {transaction.ToString()} aborted.");
            transaction.State = TransactionState.Aborted;
            foreach (var site in transaction.GetStableSites())
            {
                site.ClearLocks(transaction);
            }
        }

        private void BufferOperation(Transaction transaction, Operation operation, TransactionState state)
        {
            if (transaction.OperationBuffer == null) 
            {
                transaction.State = state;
                transaction.WaitTime = Clock.Time;
                transaction.OperationBuffer = operation;
            }
        }

        private Transaction GetTransaction(string transactionName)
        {
            var transaction = Transactions.FirstOrDefault(t => t.Name.ToLower() == transactionName.ToLower());

            if (transaction == null)
            {
                Logger.Fail($"Trying to read as transaction {transactionName}, but it doesn't exist.");
            }

            if (transaction.IsWaiting())
            {
                DeadlockManager.DetectDeadlocks(Transactions);
                RerunTransactions();
            }

            if (transaction.State != TransactionState.Running)
            {   
                Logger.Fail($"Transaction {transactionName} received another operation while it is {transaction.State}.");
            }

            return transaction;
        }
    }
}