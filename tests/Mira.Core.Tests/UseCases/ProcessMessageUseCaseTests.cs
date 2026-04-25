using Mira.Core.Interfaces;
using Mira.Core.UseCases;
using Moq;
using Xunit;

namespace Mira.Core.Tests.UseCases;

public sealed class ProcessMessageUseCaseTests
{
    [Fact]
    public async Task ExecuteAsync_ReturnsExpectedResponse()
    {
        // Arrange
        var mockLlmProvider = new Mock<ILlmProvider>();
        var expectedResponse = "This is a test response.";
        mockLlmProvider
            .Setup(p => p.GenerateResponseAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedResponse);
        var useCase = new ProcessMessageUseCase(mockLlmProvider.Object);
        var userMessage = "Hello, Mira!";

        // Act
        var actualResponse = await useCase.ExecuteAsync(userMessage, CancellationToken.None);
        
        // Assert
        Assert.Equal(expectedResponse, actualResponse);
        mockLlmProvider.Verify(p => p.GenerateResponseAsync(
            "You are Mira, a personal AI assistant. Be concise and helpful.",
            userMessage,
            It.IsAny<CancellationToken>()), Times.Once);
    }
}