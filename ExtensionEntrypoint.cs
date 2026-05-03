using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.Extensibility;

namespace DeepSeek_v4_for_VisualStudio
{
    /// <summary>
    /// Extension entrypoint for the VisualStudio.Extensibility extension.
    /// </summary>
    [VisualStudioContribution]
    internal class ExtensionEntrypoint : Extension
    {
        /// <inheritdoc/>
        public override ExtensionConfiguration ExtensionConfiguration => new()
        {
            Metadata = new(
                    id: "DeepSeek_v4_for_VisualStudio.56137d69-2af5-4c7c-a058-5343aebac4c1",
                    version: this.ExtensionAssemblyVersion,
                    publisherName: "zmy15",
                    displayName: "DeepSeek_v4_for_VisualStudio",
                    description: "DeepSeek_v4_for_VisualStudio"),
        };

        /// <inheritdoc />
        protected override void InitializeServices(IServiceCollection serviceCollection)
        {
            base.InitializeServices(serviceCollection);

            // You can configure dependency injection here by adding services to the serviceCollection.
        }
    }
}
