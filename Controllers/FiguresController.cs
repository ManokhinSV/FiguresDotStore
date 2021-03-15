using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
//Объявлены не все using

namespace FiguresDotStore.Controllers
{
	internal interface IRedisClient
	{
		int Get(string type);
		void Set(string type, int current);
	}

	public static class FiguresStorage
	{
		// корректно сконфигурированный и готовый к использованию клиент Редиса
		private static IRedisClient RedisClient { get; }
		//Отсутствует инициация свойста

		public static bool CheckIfAvailable(string type, int count)
		{
			return RedisClient.Get(type) >= count;
			//Игнорируется размер фигуры.
			//Если заказчик хочет получить круг с радиусом 1м, а на складе только с радиусом 2м
			//то проверка работает не корректно
		}

		public static void Reserve(string type, int count)
		//Игнорируется размер фигур
		{
			var current = RedisClient.Get(type);

			RedisClient.Set(type, current - count);
			//Нет смысла сначала получать данные от Redis, а потом их корректировать
			//эти две функции мжно объединить в третью, которая полностью отрабатывает на стороне Redis,
			//и получить прирост производительности
		}
	}

	public class Position
	{
		public string Type { get; set; }

		public float SideA { get; set; }
		public float SideB { get; set; }
		public float SideC { get; set; }

		public int Count { get; set; }
	}

	public class Cart
	{
		public List<Position> Positions { get; set; }
	}

	public class Order
	{
		public List<Figure> Positions { get; set; }
		//Название свойства класса "Positions" не соответствует смысловому содержанию.
		//Имеет смысл переименовать свойство в "Figures"

		public decimal GetTotal() =>
			//Неиспользуемы метод
			Positions.Select(p => p switch
			{
				Triangle => (decimal)p.GetArea() * 1.2m,
				Circle => (decimal)p.GetArea() * 0.9m
				/* 1. Пропущен реализованный класс Square
				 * 2.1. Считается, что "магические числа" следует выводить в константы
				 * 2.2. Возможно, имее смыс в реализацию фигур добавить метод по подсчету их стоимости.
				 * 3. Нужно добавить обработку на null и новые реализации фигур:
				 * null => throw new ArgumentNullException(...);
				 * _ => throw new Exception(...);
				 */
			})
				.Sum();
	}

	public abstract class Figure
	{
		public float SideA { get; set; }
		public float SideB { get; set; }
		public float SideC { get; set; }

		public abstract void Validate();
		public abstract double GetArea();
	}

	public class Triangle : Figure
	{
		public override void Validate()
		{
			bool CheckTriangleInequality(float a, float b, float c) => a < b + c;
			if (CheckTriangleInequality(SideA, SideB, SideC)
				&& CheckTriangleInequality(SideB, SideA, SideC)
				&& CheckTriangleInequality(SideC, SideB, SideA))
				return;
			throw new InvalidOperationException("Triangle restrictions not met");
		}

		public override double GetArea()
		{
			var p = (SideA + SideB + SideC) / 2;
			return Math.Sqrt(p * (p - SideA) * (p - SideB) * (p - SideC));
		}

	}

	public class Square : Figure
	{
		public override void Validate()
		{
			if (SideA < 0)
				throw new InvalidOperationException("Square restrictions not met");

			if (SideA != SideB)
				throw new InvalidOperationException("Square restrictions not met");
		}

		public override double GetArea() => SideA * SideA;
	}

	public class Circle : Figure
	{
		public override void Validate()
		{
			if (SideA < 0)
				throw new InvalidOperationException("Circle restrictions not met");
		}

		public override double GetArea() => Math.PI * SideA * SideA;
	}

	public interface IOrderStorage
	{
		// сохраняет оформленный заказ и возвращает сумму
		Task<decimal> Save(Order order);
	}

	[ApiController]
	[Route("[controller]")]
	public class FiguresController : ControllerBase
	{
		private readonly ILogger<FiguresController> _logger;
		//_logger нигде не используется
		private readonly IOrderStorage _orderStorage;

		public FiguresController(ILogger<FiguresController> logger, IOrderStorage orderStorage)
		{
			_logger = logger;
			_orderStorage = orderStorage;
		}

		// хотим оформить заказ и получить в ответе его стоимость
		[HttpPost]
		public async Task<ActionResult> Order(Cart cart)
		{
			foreach (var position in cart.Positions)
			{
				if (!FiguresStorage.CheckIfAvailable(position.Type, position.Count))
				//У нас конкурентная работа сервиса, следовательно, когда дело дойдет до резервирования,
				//то успешно пройденная проверка здесь, уже не удет устаревшей на момент резервирования
				{
					return new BadRequestResult();
					//Было бы лучше возвращать осмысленный ответ пользователю с причиной отказа оформления ордера
				}
			}

			var order = new Order
			{
				Positions = cart.Positions.Select(p =>
				{
					Figure figure = p.Type switch
					{
						"Circle" => new Circle(),
						"Triangle" => new Triangle(),
						"Square" => new Square()
						//На случай опечаток в "p.Type" или входного null нужна дополнительная обработка
						//null => ...
						//_ => ...
					};
					figure.SideA = p.SideA;
					figure.SideB = p.SideB;
					figure.SideC = p.SideC;
					figure.Validate();
					return figure;
				}).ToList()
			};

			foreach (var position in cart.Positions)
			{
				FiguresStorage.Reserve(position.Type, position.Count);
				//У нас конкурентная работа сервиса, следовательно, когда дело дойдет до резервирования,
				//то успешно пройденная проверка выше, уже может быть неактуальной
			}

			var result = _orderStorage.Save(order);
			//Необходимо дождаться результата сохранения перед отправкой данных:
			//var result = await _orderStorage.Save(order);

			return new OkObjectResult(result.Result);
		}
	}
}
