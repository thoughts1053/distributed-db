using System;
using System.Collections.Generic;
using System.Linq;
using DistributedDb.Operations;
using DistributedDb.Variables;

namespace DistributedDb.Sites
{
    public class SiteManager
    {
        public SiteManager()
        {
            var variables = new List<Variable>();
            for (int i = 1; i <= 20; i++) {
                variables.Add(new Variable(i));
            }

            Sites = new List<Site>();
            for (int i = 1; i <= 10; i++)
            {
                var data = variables
                    .Where(variable => variable.Id % 2 == 0 || i == 1 + variable.Id % 10)
                    .Select(variable => new Variable(variable.Id))
                    .ToList();

                Sites.Add(new Site(i, data));    
            }
        }
        
        public IList<Site> Sites { get; set; }

        public void Execute(IEnumerable<Operation> operations)
        {
            foreach (var operation in operations)
            {
                switch (operation.Type)
                {
                    case OperationType.Dump:
                        Dump(operation.Site, operation.Variable);
                        break;
                    default:
                        Console.WriteLine(operation.ToString());
                        break;
                }
            }
        }

        public void Dump(int? siteId, string variable)
        {
            var sites = siteId == null ? Sites : Sites.Where(s => s.Id == siteId);
            sites = string.IsNullOrWhiteSpace(variable) ? sites : SitesWithVariable(variable);

            foreach (var site in sites)
            {
                Console.WriteLine(site.Dump(variable));
            }
        }

        public List<Site> SitesWithVariable(string variable, SiteState? state = null)
        {
            var sites = Sites.Where(s => s.Data.Any(d => d.Name == variable));

            if (state != null)
            {
                sites = sites.Where(s => s.State == state);
            }

            return sites.ToList();
        }

        public List<Variable> Snapshot()
        {
            var variables = new List<Variable>();
            for (int i = 1; i <= 20; i++) {
                var variable = new Variable(i);
                SetCommittedValue(variable);
                variables.Add(variable);
            }

            return variables;
        }

        public void SetCommittedValue(Variable variable)
        {
            var stableSites = SitesWithVariable(variable.Name, SiteState.Stable);
            foreach (var site in stableSites)
            {
                // check variable is valid in case site is recovering
                variable.Value = site.ReadData(variable);
                return;
            }
        }
    }
}