// Testy TransportSecurityTests nadpisują zmienne env procesu; równoległe kolekcje dzieliłyby je
// z InstallRunnerTests. Suita jest mała — serializacja całego assembly kosztuje milisekundy.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]