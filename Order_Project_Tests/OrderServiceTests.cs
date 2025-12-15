using Moq;
using Order_Project.Models;
using Order_Project.Services;
using Order_Project.Services.Intefraces;

namespace Order_Project_Tests
{
    public class OrderServiceTests
    {
        private readonly Mock<IInventoryService> inv;
        private readonly Mock<IPaymentService> pay;
        private readonly Mock<INotificationService> note;
        private readonly Mock<IDiscountService> disc;
        private readonly OrderService service;

        public OrderServiceTests()
        {
            inv = new Mock<IInventoryService>();
            pay = new Mock<IPaymentService>();
            note = new Mock<INotificationService>();
            disc = new Mock<IDiscountService>();
            
            // За замовчуванням: Нормальні запаси доступні, оплата успішна, ручне підтвердження оплати не потребується
            inv.Setup(x => x.CheckStock(It.IsAny<string>(), It.IsAny<int>())).Returns(true);
            inv.Setup(x => x.ReserveStock(It.IsAny<string>(), It.IsAny<int>())).Returns(true);
            pay.Setup(x => x.ProcessPayment(It.IsAny<Order>())).Returns(true);
            pay.Setup(x => x.NeedsManualApproval(It.IsAny<Order>())).Returns(false);
            
            service = new OrderService(inv.Object, pay.Object, note.Object, disc.Object);
        }
        
        // R1. Замовлення повинні містити дату замовлення.
        [Fact]
        public void R1_CreateOrder_ShouldSetCreatedAt()
        {
            var product = "TestProduct";
            var quantity = 1;
            var price = 10m;
            
            var order = service.CreateOrder(product, quantity, price);
            
            Assert.NotEqual(default(DateTime), order.CreatedAt);
            Assert.True((DateTime.UtcNow - order.CreatedAt).TotalMinutes < 1);
        }
        
        // R2. Ви не можете замовити більше 100 одиниць на день (не на запит).
        [Fact]
        public void R2_CreateOrder_MoreThan100UnitsOrderedPerDay_ShouldFail()
        {
            var product = "TestProduct";
            var price = 10m;
            
            service.CreateOrder(product, 50, price);
            
            var ex = Assert.Throws<InvalidOperationException>(() =>
                service.CreateOrder(product, 55, price)); // > 100
            Assert.Contains("exceed", ex.Message.ToLower()); 
        }
        
        // R3. Система застосовує 10% знижку для замовлень > 10 одиниць.
        [Fact]
        public void R3_CreateOrder_MoreThan10UnitsOrdered_ShouldUseDiscount10Percent()
        {
            var product = "BulkProduct";
            var quantity = 15; // > 10
            var unitPrice = 100m;
            
            var order = service.CreateOrder(product, quantity, unitPrice);
            
            // Очікувані розрахунки:
            // Ціна товарів: 15 * 100 = 1500
            // Знижка 10%: 1500 * 0.9 = 1350
            // +20% з методу CreateOrder: 1350 * 1.20 = 1620
            var expectedTotal = (quantity * unitPrice * 0.90m) * 1.20m;
            
            Assert.Equal(expectedTotal, order.TotalPrice);
        }
        
        // R4. Система відхиляє замовлення на товари зі списку «Заборонені».
        [Fact]
        public void R4_CreateOrder_ProductInProhibitedList_ShouldFail()
        {
            var product = "ProhibitedProduct";
            var quantity = 1;
            var unitPrice = 10m;
            
            var ex = Assert.Throws<InvalidOperationException>(() => service.CreateOrder(product, quantity, unitPrice));
            Assert.Contains("prohibited", ex.Message.ToLower());
        }
        
        // R5. Платіжна система може повернути такі статуси: Успішно, Неуспішно, Затримка.
        [Fact]
        public void R5_CreateOrder_Handles_Payment_Success()
        {
            pay.Setup(p => p.ProcessPayment(It.IsAny<Order>())).Returns(true);
            
            var order = service.CreateOrder("Product", 1, 10m);
            
            Assert.Equal("Paid", order.State);
        }

        [Fact]
        public void R5_CreateOrder_Handles_Payment_Failed()
        {
            pay.Setup(p => p.ProcessPayment(It.IsAny<Order>())).Returns(false);
            
            Assert.Throws<InvalidOperationException>(() => service.CreateOrder("Product", 1, 10m));
        }
        
        [Fact]
        public void R5_CreateOrder_Handles_Payment_PendingApproval()
        {
            pay.Setup(p => p.NeedsManualApproval(It.IsAny<Order>())).Returns(true);
            
            var order = service.CreateOrder("Product", 1, 10m);
            
            Assert.Equal("PendingApproval", order.State);
        }
        
        // R6. Затримка платежу переводить замовлення в стан «Очікування оплати».
        [Fact]
        public void R6_CreateOrder_PaymentPendingApproval_ShouldSetStateToPendingApproval()
        {
            pay.Setup(p => p.NeedsManualApproval(It.IsAny<Order>())).Returns(true);
            
            var order = service.CreateOrder("ExpensiveProduct", 10, 1000m);
            
            Assert.Equal("PendingApproval", order.State);
        }
        
        // R7. Замовлення повинні мати статус (Нове, Оплачене, Очікування оплати, Скасоване).
        [Fact]
        public void R7_Order_PaidStatus()
        {
            var paidOrder = service.CreateOrder("Product", 1, 10m);
            
            Assert.Equal("Paid", paidOrder.State);
        }

        [Fact]
        public void R7_Order_PendingApprovalStatus()
        {
            pay.Setup(p => p.NeedsManualApproval(It.IsAny<Order>())).Returns(true);
            
            var pendingOrder = service.CreateOrder("Product", 1, 10m);
            
            Assert.Equal("PendingApproval", pendingOrder.State);
        }

        [Fact]
        public void R7_Order_CanceledStatus()
        {
            var order = service.CreateOrder("Product", 1, 10m);
            service.CancelOrder(order.Id);
            
            Assert.Equal("Cancelled", order.State);
        }
        
        // R8. Запаси зменшуються тільки після успішної оплати.
        [Fact]
        public void R8_CreateOrder_SuccessfulPayment_ShouldDecreaseStock()
        {
            var product = "StockItem";
            var quantity = 5;
            inv.Setup(x => x.CheckStock(product, quantity)).Returns(true);
            
            service.CreateOrder(product, quantity, 10m);
            
            inv.Verify(x => x.ReduceStock(product, quantity), Times.Once);
        }
        
        // R9. Система повинна надсилати різні повідомлення залежно від статусу замовлення.
        [Fact]
        public void R9_NotificationService_ShouldSendPaidConfirmation()
        {
            var order = service.CreateOrder("Product", 1, 10m);
            
            note.Verify(n => n.SendPaidConfirmation(It.IsAny<Order>()), Times.Once);
        }

        [Fact]
        public void R9_NotificationService_ShouldSendPendingApproval()
        {
            pay.Setup(p => p.NeedsManualApproval(It.IsAny<Order>())).Returns(true);
            
            var order = service.CreateOrder("Product", 1, 10m);
            
            note.Verify(n => n.SendPendingApproval(It.IsAny<Order>()), Times.Once);
        }

        [Fact]
        public void R9_NotificationService_ShouldSendCancellation()
        {
            var order = service.CreateOrder("Product", 1, 10m);
            
            service.CancelOrder(order.Id);
            
            note.Verify(n => n.SendCancellation(It.IsAny<Order>()), Times.Once);
        }
        
        // R10. Кожне замовлення повинно мати розраховану загальну вартість.
        [Fact]
        public void R10_Order_ShouldHaveTotalCost()
        {
            var order = service.CreateOrder("Product", 2, 50m);
            
            Assert.Equal(order.TotalPrice, (2 * 50m) * 1.2m);
        }
        
        // R11. Загальна вартість замовлення повинна враховувати застосовані знижки.
        [Fact]
        public void R11_Total_Cost_Takes_Into_Account_Discounts()
        {
            var price = 100m;
            var discountAmount = 20m;
            disc.Setup(d => d.ValidateCode("PROMO")).Returns(discountAmount);
            
            var order = service.CreateOrder("Product", 1, price, "Normal", "PROMO");
            
            // Очікувані розрахунки:
            // Ціна товару: 100
            // Знижка 20: 100 - 20 = 80
            // +20% з методу CreateOrder: 80 * 1.20 = 96
            var expected = (price - discountAmount) * 1.20m;
            Assert.Equal(expected, order.TotalPrice);
        }
        
        // R12. Система реєструє кожне замовлення в аналітичній службі.
        [Fact]
        public void R12_GetOrders_ShouldContainOrder()
        {
            var order = service.CreateOrder("Product", 1, 10m);
            
            Assert.Contains(order, service.GetOrders()); 
        }
        
        // R13. Видалення замовлення не повинно повертати запаси, якщо оплата не пройшла.
        [Fact]
        public void R13_Deleted_Order_Should_Not_Return_Stock_If_Payment_Not_Successful()
        {
            pay.Setup(p => p.NeedsManualApproval(It.IsAny<Order>())).Returns(true);
            var order = service.CreateOrder("Product", 5, 10m);
            
            service.CancelOrder(order.Id);
            
            inv.Verify(x => x.IncreaseStock(It.IsAny<string>(), It.IsAny<int>()), Times.Never);
        }
        
        // R14. Система повинна запобігати дублюванню замовлень з однаковим ідентифікатором.
        [Fact]
        public void R14_System_Prevents_Duplication_Of_Order_Identifiers()
        {
            var order1 = service.CreateOrder("Product", 1, 10m);
            var order2 = service.CreateOrder("PProduct", 1, 10m);

            Assert.NotEqual(order1.Id, order2.Id);
        }
        
        // R15. Замовлення повинні бути доступними для пошуку за статусом (наприклад, GetOrdersByStatus).
        [Fact]
        public void R15_Orders_Must_Be_Available_For_Search_By_Status()
        {
            // Arrange
            pay.Setup(p => p.NeedsManualApproval(It.IsAny<Order>())).Returns(true);
            service.CreateOrder("PendingItem", 1, 10m); // Pending
            
            pay.Setup(p => p.NeedsManualApproval(It.IsAny<Order>())).Returns(false);
            service.CreateOrder("PaidItem", 1, 10m); // Paid
            
            // Симулюємо GetOrdersByStatus через LINQ
            var orders = service.GetOrders();
            var pendingOrders = orders.Where(o => o.State == "PendingApproval").ToList();
            var paidOrders = orders.Where(o => o.State == "Paid").ToList();
            
            Assert.Single(pendingOrders);
            Assert.Single(paidOrders);
        }
    }
}
