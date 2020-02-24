import React, { useState, useEffect } from 'react';
import logo from './logo.svg';
import './App.css';

type Todo = {
  id: number;
  content: string;
};

const apiUrl = "http://localhost:7071/api";

const App = () => {
  const [todos, setTodos] = useState<Todo[]>([]);

  useEffect(() => {
    fetch(`${apiUrl}/todos`)
      .then(response => response.json())
      .then(results => setTodos(results));
  }, []);

  const handleNewTodoInput = (e: React.KeyboardEvent<HTMLInputElement>) => {
    const inputTarget = e.target as HTMLInputElement;
    const value = inputTarget.value;

    if (inputTarget && e.key && e.key.toLowerCase() === 'enter' && value) {
      const payload = {
        content: value
      };

      fetch(`${apiUrl}/todos`, {
        method: 'POST',
        body: JSON.stringify(payload)
      })
        .then(response => response.json())
        .then(todo => {
          const newTodos = todos.concat(todo);
          setTodos(newTodos);
          inputTarget.value = '';
        });
    }
  }

  const handleTodoBlur = (e: React.FocusEvent<HTMLInputElement>, id: number) => {
    const inputTarget = e.target as HTMLInputElement;

    if (inputTarget) {
      const value = inputTarget.value;

      const payload = {
        id,
        content: value
      };

      fetch(`${apiUrl}/todos`, {
        method: 'PUT',
        body: JSON.stringify(payload)
      })
        .then(response => response.json())
        .then(todo => {
          const newTodos = todos.map(t => {
            if (t.id === todo.id) {
              return todo;
            }
            return t;
          });

          setTodos(newTodos);
        });
    }
  }

  const handleRemoveTodoButtonClicked = (id: number) => {
    fetch(`${apiUrl}/todos/${id}`, {
      method: 'DELETE'
    })
      .then(_ => {
        const newTodos = todos.filter(t => t.id !== id);
        setTodos(newTodos);
      });
  }

  return (
    <div className="App">
      <header className="App-header">
        <img src={logo} className="App-logo" alt="logo" />
        <p>
          Edit <code>src/App.tsx</code> and save to reload.
        </p>

        {todos.map(todo => {
          return (<div key={todo.id}>
            <input type="text" defaultValue={todo.content} onBlur={e => handleTodoBlur(e, todo.id)} />
            <button onClick={_ => handleRemoveTodoButtonClicked(todo.id)}>
              Remove
            </button>
          </div>);
        })}

        <input type="text" onKeyPress={e => handleNewTodoInput(e)} />
      </header>
    </div>
  );
}

export default App;
