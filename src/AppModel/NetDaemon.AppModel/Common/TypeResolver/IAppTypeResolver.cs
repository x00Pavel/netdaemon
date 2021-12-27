namespace NetDaemon.AppModel.Common.TypeResolver;

/// <summary>
///     Implementers of this interface returns all types from
///     any source like the current assembly or a dynamically compiled assembly
/// </summary>
public interface IAppTypeResolver
{
    /// <summary>
    ///     Returns all types
    /// </summary>
    IReadOnlyCollection<Type> GetTypes();
}