// LocalDB creates one isolated database per integration test; bound concurrent creators.
[assembly: Parallelize(Workers = 2, Scope = ExecutionScope.MethodLevel)]
