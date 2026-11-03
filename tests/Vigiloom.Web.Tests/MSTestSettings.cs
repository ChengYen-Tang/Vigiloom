// Web integration cases create isolated databases on one shared SQL instance.
[assembly: Parallelize(Workers = 2, Scope = ExecutionScope.MethodLevel)]
