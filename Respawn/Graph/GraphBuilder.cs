using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace Respawn.Graph
{
    public class GraphBuilder
    {
        public GraphBuilder(HashSet<Table> tables, HashSet<Relationship> relationships, bool primitiveDb = false)
        {
            FillRelationships(tables, relationships, primitiveDb);
            (HashSet<Relationship> cyclicRelationships, Stack<Table> toDelete) result;
            if (primitiveDb)
                result = FindPrimitiveCycles(tables);
            else
                result = FindAndRemoveCycles(tables);

            ToDelete = new ReadOnlyCollection<Table>(result.toDelete.ToList());

            CyclicalTableRelationships = new ReadOnlyCollection<Relationship>(result.cyclicRelationships.ToList());
        }

        public ReadOnlyCollection<Table> ToDelete { get; }
        public ReadOnlyCollection<Relationship> CyclicalTableRelationships { get; }

        private static void FillRelationships(HashSet<Table> tables, HashSet<Relationship> relationships, bool primitiveDb)
        {
            foreach (var relationship in relationships)
            {
                var parentTable = tables.SingleOrDefault(t => t == relationship.ParentTable);                
                if (primitiveDb && parentTable == null)
                {
                    parentTable = relationship.ParentTable;
                    parentTable.SeedColumn = "RemoveBeforeDeleteScript";
                    tables.Add(parentTable);
                }
                var refTable = tables.SingleOrDefault(t => t == relationship.ReferencedTable);
                bool ommit = false;
                if (!primitiveDb)
                    ommit = parentTable == refTable;
                if (parentTable != null && refTable != null && !ommit)
                {
                    parentTable.Relationships.Add(new Relationship(parentTable, refTable, relationship.Name, relationship.ParentColumnName, relationship.ReferencedColumnName));
                }
            }
        }

        private static (HashSet<Relationship> cyclicRelationships, Stack<Table> toDelete) 
            FindAndRemoveCycles(HashSet<Table> allTables)
        {
            var notVisited = new HashSet<Table>(allTables);
            var visiting = new HashSet<Table>();
            var visited = new HashSet<Table>();
            var cyclicRelationships = new HashSet<Relationship>();
            var toDelete = new Stack<Table>();            

            foreach (var table in allTables)
            {
                HasCycles(table, notVisited, visiting, visited, toDelete, cyclicRelationships);
            }
            return (cyclicRelationships, toDelete);
        }

        private static (HashSet<Relationship> cyclicRelationships, Stack<Table> toDelete)
            FindPrimitiveCycles(HashSet<Table> allTables)
        {
            var notVisited = new HashSet<Table>(allTables);
            var cyclicRelationships = new HashSet<Relationship>();
            var toDelete = new Stack<Table>();
            var toKill = new Stack<Table>();

            foreach (var table in allTables)
            {
                if (table.Relationships.Count > 0)
                {
                    foreach (var relationship in table.Relationships)
                    {
                        cyclicRelationships.Add(relationship);
                    }
                }
                notVisited.Remove(table);
                if (table.SeedColumn == "RemoveBeforeDeleteScript")
                    toKill.Push(table);
                else
                    toDelete.Push(table);
            }
            foreach (Table table in toKill)
            {
                allTables.Remove(table);
            }

            return (cyclicRelationships, toDelete);
        }

        private static bool HasCycles(Table table,
            HashSet<Table> notVisited,
            HashSet<Table> visiting,
            HashSet<Table> visited,
            Stack<Table> toDelete,
            HashSet<Relationship> cyclicalRelationships
            )
        {
            if (visited.Contains(table))
                return false;

            if (visiting.Contains(table))
                return true;

            notVisited.Remove(table);
            visiting.Add(table);

            foreach (var relationship in table.Relationships)
            {
                if (HasCycles(relationship.ReferencedTable, 
                    notVisited, visiting, visited, toDelete, cyclicalRelationships))
                {
                    cyclicalRelationships.Add(relationship);
                }
            }

            visiting.Remove(table);
            visited.Add(table);
            toDelete.Push(table);

            return false;
        }
    }
}